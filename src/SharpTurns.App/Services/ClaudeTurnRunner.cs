using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.Services;

/// <summary>
/// Turn progress for display. The notifications run off the UI thread in the order the runner saw the events, and
/// add display items by the same rule the runner uses for saved parts: text continues the last text item, and every
/// tool call, question, or delivered message starts a new item. AskAsync and ApproveAsync must show their dialogs on
/// the UI thread and close them when the token is canceled. Started reports the running turn once it is saved.
/// </summary>
internal sealed record TurnCallbacks(
    Action<ConversationTurn> Started,
    Action<string> TextDelta,
    Action<string> StatusChanged,
    Action<ToolCallRecord> ToolChanged,
    Action<string, QuestionRecord, bool> QuestionChanged,
    Action<ClaudeCliUserMessage> UserMessageDelivered,
    Action<TurnUsage> UsageChanged,
    Action<ClaudeCliRateLimitSnapshot> RateLimitsChanged,
    Func<ClaudeCliQuestion, CancellationToken, Task<string?>> AskAsync,
    Func<string, string, CancellationToken, Task<bool>> ApproveAsync);

/// <summary>
/// The saved turn, any messages queued during it that never reached the CLI, why each optional context file that
/// couldn't be read was left out, and the additional folders skipped because they aren't existing absolute directories.
/// </summary>
internal sealed record TurnRunResult(ConversationTurn Turn, IReadOnlyList<string> UndeliveredMessages,
    IReadOnlyList<string> OmittedContextFiles, IReadOnlyList<string> SkippedFolders);

/// <summary>
/// Runs one turn through the CLI: saves the prompt, resumes the conversation's CLI session or seeds a fresh one
/// from the visible history, streams the response, and saves how the turn ended. executable returns the CLI to launch;
/// it's read for each turn, so a changed setting applies to the next one, and null runs claude from the PATH.
/// </summary>
internal sealed class ClaudeTurnRunner(ConversationStore store, Func<string?> executable)
{
    public const string ClaudeCliNotStarted =
        "Couldn't start the claude CLI. Install it and make sure it's on your PATH, or set its path in Config → Preferences.";

    /// <summary>The ask rules, one per line; unset uses <see cref="ClaudeCliCodingPolicy.DefaultAskRules"/>.</summary>
    public const string AskRulesSetting = "ask_rules";

    public ClaudeTurnRunner(ConversationStore store, string? executable = null) : this(store, () => executable) { }

    public static IReadOnlyList<string> AskRules(string? setting) => setting is null ? ClaudeCliCodingPolicy.DefaultAskRules
        : setting.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns the saved turn, including after Stop or failure. Throws only if the database fails.</summary>
    public async Task<TurnRunResult> RunAsync(Project project, Conversation conversation, string prompt,
        IReadOnlyList<ImageAttachment> images, string systemPrompt, ClaudeCliInputQueue queue, TurnCallbacks callbacks,
        CancellationToken token)
    {
        var turn = await store.StartTurnAsync(conversation.Id, prompt, images, token).ConfigureAwait(false);
        callbacks.Started(turn);
        // Every field below is guarded by gate: CLI events and permission requests arrive on different threads.
        var gate = new object();
        var items = new List<Item>();
        var tools = new Dictionary<string, Item>(StringComparer.Ordinal);
        var questions = new Dictionary<string, Item>(StringComparer.Ordinal);
        var thinking = new StringBuilder();
        var activity = new ClaudeCliActivity();
        var usage = new ClaudeCliUsageTracker();
        var submitted = new Dictionary<string, string>(StringComparer.Ordinal);
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        // The CLI can send requests concurrently; show one dialog at a time.
        using var interaction = new SemaphoreSlim(1, 1);
        var finished = false;
        var textBlockStarted = false;
        var thinkingBlockStarted = false;
        var receivedText = false;
        string? model = null;
        ClaudeCodeSessionState? state = null;
        string[] mcpServerNames = [];
        string? contextFilesFingerprint = null;
        string[] omittedContextFiles = [];
        var skippedFolders = new List<string>();
        TurnStatus status;
        string? error = null;

        void AppendText(string text)
        {
            if (finished) return;
            // Text blocks within one item keep a blank line between them.
            string delta;
            if (items.LastOrDefault() is { Role: "assistant", Type: TurnParts.Text } last)
            {
                delta = (textBlockStarted && last.Text.Length > 0 ? "\n\n" : "") + text;
                last.Text.Append(delta);
            }
            else
            {
                delta = text;
                items.Add(Item.WithText("assistant", text));
            }
            textBlockStarted = false;
            callbacks.TextDelta(delta);
        }

        void PublishTools()
        {
            foreach (var change in activity.TakeChanges())
            {
                var tool = new ToolCallRecord(change.ToolUseId, change.Name, change.Input, change.Result, change.Status, change.IsError);
                if (tools.TryGetValue(tool.Id, out var item))
                {
                    var previous = (ToolCallRecord)item.Value!;
                    tool = tool with { RequestInputTokens = previous.RequestInputTokens, RequestCachedTokens = previous.RequestCachedTokens };
                }
                else
                {
                    tools[tool.Id] = item = new("assistant", TurnParts.Tool);
                    items.Add(item);
                    // A call first appears while the request that made it streams, so the latest request is its own.
                    var request = usage.Current;
                    tool = tool with { RequestInputTokens = request.ContextTokens, RequestCachedTokens = request.ContextCachedTokens };
                }
                item.Value = tool;
                callbacks.ToolChanged(tool);
            }
        }

        void SetQuestion(string key, QuestionRecord question, bool waiting)
        {
            lock (gate)
            {
                if (finished) return;
                if (!questions.TryGetValue(key, out var item))
                {
                    questions[key] = item = new("assistant", TurnParts.Question);
                    items.Add(item);
                }
                item.Value = question;
                callbacks.QuestionChanged(key, question, waiting);
            }
        }

        async Task<T> InteractAsync<T>(Func<Task<T>> show, string waitingStatus, CancellationToken ct)
        {
            await interaction.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                callbacks.StatusChanged(waitingStatus);
                return await show().ConfigureAwait(false);
            }
            finally
            {
                interaction.Release();
                callbacks.StatusChanged("Working…");
            }
        }

        async Task<ClaudeCliPermissionDecision> AnswerAsync(ClaudeCliPermissionRequest request, CancellationToken ct)
        {
            if (request.ToolName == "AskUserQuestion") return await AskQuestionsAsync(request, ct).ConfigureAwait(false);
            if (!ClaudeCliCodingPolicy.AllowsAutomatically(request.ToolName, mcpServerNames))
                return new(false, Message: "Tool is outside the enabled coding scope.");
            // The CLI runs preapproved tools on its own. It asks the host only for its safety checks, such as
            // edits to its own settings files, and the user decides those.
            lock (gate)
            {
                activity.Permission(request, "Waiting for your approval");
                PublishTools();
            }
            var allowed = false;
            try
            {
                allowed = await InteractAsync(() => callbacks.ApproveAsync(request.ToolName, request.Input.GetRawText(), ct),
                    "Waiting for your approval…", ct).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    activity.Permission(request, ct.IsCancellationRequested ? "Request canceled" : allowed ? "Approved" : "Denied by you",
                        final: !allowed);
                    PublishTools();
                }
            }
            return allowed ? new(true) : new(false, Message: "The user denied this tool request.");
        }

        async Task<ClaudeCliPermissionDecision> AskQuestionsAsync(ClaudeCliPermissionRequest request, CancellationToken ct)
        {
            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var question in ClaudeCliQuestions.Parse(request.Input))
            {
                var key = Guid.NewGuid().ToString("N");
                var shown = QuestionMarkdown(question);
                SetQuestion(key, new(shown, null), waiting: true);
                string? answer = null;
                try
                {
                    answer = await InteractAsync(() => callbacks.AskAsync(question, ct), "Waiting for your answer…", ct)
                        .ConfigureAwait(false);
                }
                finally { SetQuestion(key, new(shown, string.IsNullOrWhiteSpace(answer) ? null : answer.Trim()), waiting: false); }
                if (string.IsNullOrWhiteSpace(answer)) return new(false, Message: "The user declined to answer.");
                answers[question.Text] = answer.Trim();
            }
            return new(true, ClaudeCliQuestions.Answer(request.Input, answers));
        }

        IReadOnlyList<ClaudeCliUserMessage> TakeQueued()
        {
            var messages = queue.Take();
            lock (gate)
                foreach (var message in messages) submitted[message.Uuid] = message.Text;
            return messages;
        }

        void OnEvent(ClaudeCliEvent e)
        {
            lock (gate)
            {
                if (finished) return;
                // The init event names the resolved model, such as claude-haiku-4-5-20251001 for the haiku alias.
                if (e.Type == "system" && ClaudeCliProtocol.String(e.Data, "subtype") == "init")
                    model ??= ClaudeCliProtocol.String(e.Data, "model");
                if (e.Type == "user" && e.Data.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True
                    && ClaudeCliProtocol.String(e.Data, "uuid") is { } uuid
                    && submitted.TryGetValue(uuid, out var message) && delivered.Add(uuid))
                {
                    items.Add(Item.WithText("user", message));
                    callbacks.UserMessageDelivered(new(uuid, message));
                }
                if (e.Type == "result")
                {
                    // A cycle that streamed no text still reports its final text here.
                    if (!receivedText && e.Data.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.False
                        && ClaudeCliProtocol.String(e.Data, "result") is { Length: > 0 } finalText)
                    {
                        textBlockStarted = true;
                        AppendText(finalText);
                    }
                    receivedText = false;
                }
                if (ClaudeCliRateLimitSnapshot.FromEvent(e) is { } limits) callbacks.RateLimitsChanged(limits);
                if (usage.Observe(e)) callbacks.UsageChanged(ToTurnUsage(usage.Current));
                activity.Observe(e);
                PublishTools();
                if (TopLevel(e.Data) && e.Type == "stream_event" && e.Data.TryGetProperty("event", out var stream)
                    && ClaudeCliProtocol.String(stream, "type") == "content_block_start"
                    && stream.TryGetProperty("content_block", out var block))
                {
                    switch (ClaudeCliProtocol.String(block, "type"))
                    {
                        case "text":
                            textBlockStarted = true;
                            callbacks.StatusChanged("Responding…");
                            break;
                        case "thinking":
                            thinkingBlockStarted = true;
                            callbacks.StatusChanged("Thinking…");
                            break;
                        case "tool_use":
                            callbacks.StatusChanged($"Using {ClaudeCliProtocol.String(block, "name") ?? "a tool"}…");
                            break;
                    }
                }
                if (e.Thinking is { Length: > 0 } reasoning)
                {
                    if (thinkingBlockStarted && thinking.Length > 0) thinking.Append("\n\n");
                    thinkingBlockStarted = false;
                    thinking.Append(reasoning);
                }
                if (e.Text is { Length: > 0 } text)
                {
                    receivedText = true;
                    AppendText(text);
                }
            }
        }

        try
        {
            var directory = Path.GetFullPath(conversation.WorkspaceFor(project));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(conversation.WorkingDirectory is null
                    ? $"The project's working directory doesn't exist: {directory}"
                    : $"The conversation's workspace doesn't exist: {directory}");
            // Selected, enabled servers in a stable order so the CLI's tool definitions keep a cacheable prefix.
            var mcpServers = (await store.ListConversationMcpServersAsync(conversation.Id, token).ConfigureAwait(false))
                .Where(server => server.Enabled).OrderBy(server => server.Name, StringComparer.Ordinal).Select(ToCli).ToArray();
            // Set before the CLI starts, so permission requests always see it.
            mcpServerNames = mcpServers.Select(server => server.Name).ToArray();
            // Read on every turn, before the session is chosen: a changed file starts a fresh session that sends it.
            var contextFiles = await ContextFiles.ResolveAsync(
                await store.ListContextFilesAsync(conversation.Id, token).ConfigureAwait(false), directory, token).ConfigureAwait(false);
            ContextFiles.ThrowIfRequiredFailed(contextFiles);
            contextFilesFingerprint = ContextFiles.Fingerprint(contextFiles);
            omittedContextFiles = contextFiles.Where(f => f.Content is null).Select(f => $"{f.File.Path}: {f.Error}").ToArray();
            // Permission rules apply per launch, so a change needs no new session.
            var askRules = AskRules(await store.GetSettingAsync(AskRulesSetting, token).ConfigureAwait(false));
            var previous = await store.LoadClaudeCodeSessionAsync(conversation.Id, token).ConfigureAwait(false);
            // The deny rules apply per launch. Claude can't see them, so a note tells it the access: on a fresh session
            // once the conversation has had any, and on a resumed one when it changed, which keeps the session's working
            // context. The fingerprint stays set after access is cleared, because replayed history can still name it.
            var folderAccess = ResolveFolderAccess(conversation, skippedFolders);
            var folderAccessNote = ClaudeCliCodingPolicy.FolderAccessNote(folderAccess);
            var folderAccessFingerprint = folderAccess is null && previous?.FolderAccessFingerprint is null ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folderAccessNote)));
            var history = (await store.LoadTurnsAsync(conversation.Id, token).ConfigureAwait(false))
                .Where(t => t.Id != turn.Id).ToArray();
            var fingerprint = ClaudeCodeContext.Fingerprint(history, contextFilesFingerprint);
            var policyVersion = ClaudeCliCodingPolicy.VersionFor(systemPrompt, conversation.OutputStyle, mcpServerNames);
            var resume = ClaudeCodeContext.CanResume(previous, directory, fingerprint, policyVersion) ? previous!.SessionId : null;
            var note = resume is null
                ? folderAccessFingerprint is null ? null : folderAccessNote
                : previous!.FolderAccessFingerprint == folderAccessFingerprint ? null : folderAccessNote;
            var retainedHistory = resume is null
                ? ClaudeCodeContext.SeedHistoryBlocks(history).Select(block => new ClaudeCliHistoryBlock(block.Text, ToCli(block.Images))).ToArray()
                : null;
            // A resumed session already holds the unchanged files.
            var contextFileBlocks = resume is null
                ? contextFiles.Where(f => f.Content is not null).Select(f => ContextFiles.Format(f.File, f.Content!)).ToArray()
                : null;
            state = new(resume, directory, fingerprint, InFlight: true, policyVersion, folderAccessFingerprint);
            // Persist the uncertainty before launch. A crash cannot silently resume stale native context.
            await store.SaveClaudeCodeSessionAsync(conversation.Id, state, token).ConfigureAwait(false);
            callbacks.StatusChanged(resume is not null ? "Resuming the CLI session…"
                : retainedHistory is not { Length: > 0 } ? "Starting a new CLI session…"
                : "Starting a new CLI session from the conversation history…");

            var result = await new ClaudeCliClient(executable()).RunTurnAsync(directory, prompt, resume, OnEvent, AnswerAsync,
                systemPrompt, token, conversation.Model, conversation.Effort, sessionName: conversation.Title,
                retainedHistory: retainedHistory, images: ToCli(images), contextFiles: contextFileBlocks,
                queuedInput: queue.ToTurnInput(TakeQueued),
                outputStyle: conversation.OutputStyle, mcpServers: mcpServers, askRules: askRules,
                folderAccess: folderAccess, note: note).ConfigureAwait(false);
            state = state with { SessionId = result.SessionId };
            status = result.IsError ? TurnStatus.Failed : TurnStatus.Completed;
            if (result.IsError)
                error = result.Data.TryGetProperty("errors", out var errors) ? errors.ToString()
                    : ClaudeCliProtocol.String(result.Data, "result") ?? "The CLI reported an error.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = TurnStatus.Stopped; }
        catch (Win32Exception)
        {
            status = TurnStatus.Failed;
            error = ClaudeCliNotStarted;
        }
        catch (Exception e)
        {
            status = TurnStatus.Failed;
            error = e.Message;
        }

        queue.Close();
        var parts = new List<TurnPart>();
        string[] undelivered;
        lock (gate)
        {
            activity.Finish(status switch
            {
                TurnStatus.Stopped => "Stopped before a result",
                TurnStatus.Failed => "Turn failed before a result",
                _ => "Result not reported",
            });
            PublishTools();
            finished = true;
            var sequence = turn.Parts.Max(p => p.Sequence) + 1;
            if (thinking.Length > 0) parts.Add(new(sequence++, "assistant", TurnParts.Thinking, thinking.ToString()));
            foreach (var item in items)
                parts.Add(item.Value switch
                {
                    ToolCallRecord tool => TurnParts.FromTool(sequence++, tool),
                    QuestionRecord question => TurnParts.FromQuestion(sequence++, question),
                    _ => new(sequence++, item.Role, TurnParts.Text, item.Text.ToString()),
                });
            undelivered = [.. submitted.Where(m => !delivered.Contains(m.Key)).Select(m => m.Value), .. queue.Take().Select(m => m.Text)];
        }
        var turnUsage = usage.Current is { InputTokens: > 0 } or { ContextTokens: not null } ? ToTurnUsage(usage.Current) : null;
        // Save Stopped turns too, so this must not use the turn's token.
        await store.FinishTurnAsync(turn.Id, status, error, parts, turnUsage, model, CancellationToken.None).ConfigureAwait(false);
        var turns = await store.LoadTurnsAsync(conversation.Id, CancellationToken.None).ConfigureAwait(false);
        // Reuse the session next turn only after a clean finish, and only if the history the CLI saw is unchanged.
        // Otherwise InFlight stays set and the next turn reseeds from the saved history. The files are the ones sent; the
        // next turn reads them again and reseeds if they changed.
        if (status == TurnStatus.Completed && state is not null
            && ClaudeCodeContext.Fingerprint(turns.Where(t => t.Id != turn.Id).ToArray(), contextFilesFingerprint) == state.ContextFingerprint)
            await store.SaveClaudeCodeSessionAsync(conversation.Id,
                state with { ContextFingerprint = ClaudeCodeContext.Fingerprint(turns, contextFilesFingerprint), InFlight = false },
                CancellationToken.None).ConfigureAwait(false);
        return new(turns.Single(t => t.Id == turn.Id), undelivered, omittedContextFiles, skippedFolders);
    }

    // --add-dir rejects a missing directory, so skip it and report it rather than failing the launch.
    private static ClaudeCliFolderAccess? ResolveFolderAccess(Conversation conversation, List<string> skipped)
    {
        string[] Existing(string? list)
        {
            var existing = new List<string>();
            foreach (var path in Conversation.Lines(list))
            {
                if (Path.IsPathFullyQualified(path) && Directory.Exists(path)) existing.Add(Path.GetFullPath(path));
                else skipped.Add(path);
            }
            return existing.ToArray();
        }
        var readOnly = Existing(conversation.ReadOnlyFolders);
        var readWrite = Existing(conversation.ReadWriteFolders);
        var blocked = Conversation.Lines(conversation.BlockedPathPatterns);
        return readOnly.Length + readWrite.Length + blocked.Length == 0 ? null : new(readOnly, readWrite, blocked);
    }

    /// <summary>The question as shown on its card and replayed in later context.</summary>
    internal static string QuestionMarkdown(ClaudeCliQuestion question)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(question.Header)) text.Append("**").Append(question.Header.Trim()).Append("**\n\n");
        text.Append(question.Text.Trim());
        if (question.Options.Count > 0) text.Append("\n");
        foreach (var option in question.Options)
        {
            text.Append("\n- **").Append(option.Label).Append("**");
            if (!string.IsNullOrWhiteSpace(option.Description)) text.Append(": ").Append(option.Description.Trim());
        }
        return text.ToString();
    }

    private static ClaudeCliImage[] ToCli(IReadOnlyList<ImageAttachment> images) =>
        images.Select(image => new ClaudeCliImage(image.MediaType, image.Data)).ToArray();

    // The Config tab validated the JSON when it was saved; an environment value that isn't a string passes as its JSON.
    private static ClaudeCliMcpServer ToCli(McpServer server)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (server.EnvJson is not null && JsonNode.Parse(server.EnvJson) is JsonObject values)
            foreach (var (name, value) in values)
                environment[name] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
        return new(server.Name, JsonSerializer.Deserialize<string[]>(server.CommandJson)
            ?? throw new InvalidDataException($"MCP server {server.Name} has no command."), environment, server.WorkingDirectory);
    }

    private static TurnUsage ToTurnUsage(ClaudeCliUsage usage) =>
        new(usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.ContextTokens,
            usage.Requests > 0 ? usage.Requests : null, usage.FirstRequestInputTokens, usage.FirstRequestCachedTokens);

    private static bool TopLevel(JsonElement data) =>
        !data.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null;

    /// <summary>One response part in display order. Text items grow; tool and question items are replaced in place.</summary>
    private sealed class Item(string role, string type)
    {
        public string Role { get; } = role;
        public string Type { get; } = type;
        public StringBuilder Text { get; } = new();
        public object? Value { get; set; }

        public static Item WithText(string role, string text)
        {
            var item = new Item(role, TurnParts.Text);
            item.Text.Append(text);
            return item;
        }
    }
}
