using System.ComponentModel;
using System.Text;
using System.Text.Json;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.Services;

/// <summary>Turn progress for display. Both callbacks run off the UI thread.</summary>
internal sealed record TurnCallbacks(Action<string> TextDelta, Action<string> StatusChanged);

/// <summary>
/// Runs one turn through the CLI: saves the prompt, resumes the conversation's CLI session or seeds a fresh one
/// from the visible history, streams the response, and saves how the turn ended.
/// </summary>
internal sealed class ClaudeTurnRunner(ConversationStore store, string? executable = null)
{
    /// <summary>Returns the saved turn, including after Stop or failure. Throws only if the database fails.</summary>
    public async Task<ConversationTurn> RunAsync(Project project, Conversation conversation, string prompt,
        string systemPrompt, TurnCallbacks callbacks, CancellationToken token)
    {
        var turn = await store.StartTurnAsync(conversation.Id, prompt, token).ConfigureAwait(false);
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        ClaudeCodeSessionState? state = null;
        TurnStatus status;
        string? error = null;
        try
        {
            var directory = Path.GetFullPath(project.WorkingDirectory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"The project's working directory doesn't exist: {directory}");
            var history = (await store.LoadTurnsAsync(conversation.Id, token).ConfigureAwait(false))
                .Where(t => t.Id != turn.Id).ToArray();
            var previous = await store.LoadClaudeCodeSessionAsync(conversation.Id, token).ConfigureAwait(false);
            var fingerprint = ClaudeCodeContext.Fingerprint(history);
            var policyVersion = ClaudeCliCodingPolicy.VersionFor(systemPrompt);
            var resume = ClaudeCodeContext.CanResume(previous, directory, fingerprint, policyVersion) ? previous!.SessionId : null;
            var retainedHistory = resume is null
                ? ClaudeCodeContext.SeedHistoryBlocks(history).Select(text => new ClaudeCliHistoryBlock(text)).ToArray()
                : null;
            state = new(resume, directory, fingerprint, InFlight: true, policyVersion);
            // Persist the uncertainty before launch. A crash cannot silently resume stale native context.
            await store.SaveClaudeCodeSessionAsync(conversation.Id, state, token).ConfigureAwait(false);
            callbacks.StatusChanged(resume is not null ? "Resuming the CLI session…"
                : history.Length == 0 ? "Starting a new CLI session…"
                : "Starting a new CLI session from the conversation history…");

            var textBlockStarted = false;
            var thinkingBlockStarted = false;
            var receivedText = false;
            void AppendText(string text)
            {
                // The response keeps block separators between the assistant's text blocks.
                var separator = textBlockStarted && content.Length > 0 ? "\n\n" : "";
                textBlockStarted = false;
                content.Append(separator).Append(text);
                callbacks.TextDelta(separator + text);
            }
            var result = await new ClaudeCliClient(executable).RunTurnAsync(directory, prompt, resume, e =>
            {
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
            }, AnswerAsync, systemPrompt, token, conversation.Model, conversation.Effort,
                sessionName: conversation.Title, retainedHistory: retainedHistory).ConfigureAwait(false);
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
            error = "Couldn't start the claude CLI. Install it and make sure it's on your PATH.";
        }
        catch (Exception e)
        {
            status = TurnStatus.Failed;
            error = e.Message;
        }

        var parts = new List<TurnPart>();
        if (thinking.Length > 0) parts.Add(new(parts.Count + 2, "assistant", "thinking", thinking.ToString()));
        if (content.Length > 0) parts.Add(new(parts.Count + 2, "assistant", "text", content.ToString()));
        // Save Stopped turns too, so this must not use the turn's token.
        await store.FinishTurnAsync(turn.Id, status, error, parts, CancellationToken.None).ConfigureAwait(false);
        var turns = await store.LoadTurnsAsync(conversation.Id, CancellationToken.None).ConfigureAwait(false);
        // Reuse the session next turn only after a clean finish, and only if the history the CLI saw is unchanged.
        // Otherwise InFlight stays set and the next turn reseeds from the saved history.
        if (status == TurnStatus.Completed && state is not null
            && ClaudeCodeContext.Fingerprint(turns.Where(t => t.Id != turn.Id).ToArray()) == state.ContextFingerprint)
            await store.SaveClaudeCodeSessionAsync(conversation.Id,
                state with { ContextFingerprint = ClaudeCodeContext.Fingerprint(turns), InFlight = false },
                CancellationToken.None).ConfigureAwait(false);
        return turns.Single(t => t.Id == turn.Id);
    }

    // AskUserQuestion needs the question dialog, which is not built yet.
    private static Task<ClaudeCliPermissionDecision> AnswerAsync(ClaudeCliPermissionRequest request, CancellationToken token) =>
        Task.FromResult(ClaudeCliCodingPolicy.AllowsAutomatically(request.ToolName)
            ? new ClaudeCliPermissionDecision(true)
            : request.ToolName == "AskUserQuestion"
                ? new ClaudeCliPermissionDecision(false, Message: "This app can't show questions yet. Ask in your response text instead.")
                : new ClaudeCliPermissionDecision(false, Message: "Tool is outside the enabled coding scope."));

    private static bool TopLevel(JsonElement data) =>
        !data.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null;
}
