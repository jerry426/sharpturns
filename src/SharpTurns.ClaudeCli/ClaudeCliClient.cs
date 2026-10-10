using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpTurns.ClaudeCli;

/// <summary>
/// One process per turn; callers retain SessionId and working directory between turns.
/// Turns run the reviewed native coding tools, and any MCP servers the caller selects, with any additional folders the
/// caller grants, and append the host's system prompt; the CLI discovers the project's CLAUDE.md itself.
/// Callbacks run off the UI thread; UI hosts must marshal observable changes.
/// </summary>
public sealed class ClaudeCliClient
{
    private const int FrameCharacterLimit = 1024 * 1024;
    private readonly string _executable;
    private int _running;

    public ClaudeCliClient(string? executable = null) => _executable = executable ?? DefaultExecutable;

    /// <summary>claude from the PATH.</summary>
    internal static string DefaultExecutable => OperatingSystem.IsWindows() ? "claude.exe" : "claude";

    public async Task<ClaudeCliResult> RunTurnAsync(
        string workingDirectory,
        string prompt,
        string? sessionId,
        Action<ClaudeCliEvent> onEvent,
        Func<ClaudeCliPermissionRequest, CancellationToken, Task<ClaudeCliPermissionDecision>> onPermission,
        string systemPrompt,
        CancellationToken cancellationToken = default,
        string? model = null,
        string? effort = null,
        string? sessionName = null,
        Action<long>? onInputPayloadSent = null,
        IReadOnlyList<ClaudeCliHistoryBlock>? retainedHistory = null,
        IReadOnlyList<ClaudeCliImage>? images = null,
        IReadOnlyList<string>? contextFiles = null,
        ClaudeCliTurnInput? queuedInput = null,
        string? outputStyle = null,
        IReadOnlyList<ClaudeCliMcpServer>? mcpServers = null,
        IReadOnlyList<string>? askRules = null,
        ClaudeCliFolderAccess? folderAccess = null,
        string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        if (model is not null) ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var addDirectories = folderAccess?.ReadOnlyDirectories.Concat(folderAccess.ReadWriteDirectories).ToArray();
        if (addDirectories?.Any(directory => !Path.IsPathFullyQualified(directory)) == true)
            throw new ArgumentException("Additional directories must be absolute paths.", nameof(folderAccess));
        var settings = ClaudeCliCodingPolicy.SettingsFor(outputStyle, askRules, folderAccess);
        if (mcpServers is { Count: 0 }) mcpServers = null;
        var mcpConfig = mcpServers is null ? null : McpConfig(mcpServers);
        if (sessionName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        if (effort is not null && effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Unsupported Claude CLI effort level.", nameof(effort));
        if (sessionId is not null && !Guid.TryParse(sessionId, out _))
            throw new ArgumentException("Resume requires an explicit session UUID.", nameof(sessionId));
        if (sessionId is not null) sessionId = Guid.Parse(sessionId).ToString();
        // Snapshot/validate the input before launching; caller mutations cannot change the frame.
        var initialUuid = Guid.NewGuid().ToString();
        var userMessage = ClaudeCliProtocol.UserMessage(prompt, sessionId, retainedHistory, images, contextFiles, initialUuid, note);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(workingDirectory);
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(_executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Use the native claude.exe, not a shell or .cmd wrapper.");
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A turn is already running on this client.");

        string? mcpConfigPath = null;
        try
        {
            // A file keeps server environment values out of the process list; only this user can read it.
            if (mcpConfig is not null)
            {
                mcpConfigPath = Path.Combine(Path.GetTempPath(), $"sharpturns-mcp-{Guid.NewGuid():N}.json");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new StreamWriter(mcpConfigPath, new UTF8Encoding(false), options))
                    await file.WriteAsync(mcpConfig).ConfigureAwait(false);
            }
            return await RunProcessAsync(workingDirectory, userMessage, initialUuid, sessionId, onEvent, onPermission, cancellationToken, model, effort, sessionName, onInputPayloadSent, queuedInput,
                    systemPrompt: systemPrompt, settings: settings,
                    mcp: mcpConfigPath is null ? null : new(mcpConfigPath, mcpServers!.Select(s => s.Name).ToArray()),
                    addDirectories: addDirectories)
                .ConfigureAwait(false);
        }
        finally
        {
            // The process has exited or been killed here; it reads the configuration only at startup.
            if (mcpConfigPath is not null)
                try { File.Delete(mcpConfigPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Volatile.Write(ref _running, 0);
        }
    }

    private sealed record McpLaunch(string ConfigPath, IReadOnlyList<string> ServerNames);

    private static string McpConfig(IReadOnlyList<ClaudeCliMcpServer> servers)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
            if (server.Command.Count == 0 || string.IsNullOrWhiteSpace(server.Command[0]))
                throw new ArgumentException($"MCP server {server.Name} has no command.");
            if (!prefixes.Add(ClaudeCliCodingPolicy.McpToolPrefix(server.Name)))
                throw new ArgumentException($"MCP server {server.Name}'s tool names would clash with another selected server's.");
            IEnumerable<string> command = server.Command;
            if (server.WorkingDirectory is { } directory)
            {
                // The CLI's stdio configuration has no working directory (verified with CLI 2.1.288, which ignores
                // "cwd"); env -C sets it without a shell.
                if (OperatingSystem.IsWindows())
                    throw new NotSupportedException($"MCP server {server.Name} sets a working directory, which isn't supported on Windows. Clear it in Config → MCP Servers.");
                command = new[] { "/usr/bin/env", "-C", directory }.Concat(command);
            }
            var argv = command.ToArray();
            entries[server.Name] = new { type = "stdio", command = argv[0], args = argv[1..], env = server.Environment };
        }
        return JsonSerializer.Serialize(new { mcpServers = entries });
    }

    /// <summary>
    /// Disposable, tool-less request that never resumes or persists a session, so it cannot affect a conversation's
    /// native context or the workspace. Returns the final assistant text.
    /// </summary>
    public async Task<string> RunOneShotAsync(
        string systemPrompt,
        string prompt,
        IReadOnlyList<ClaudeCliHistoryBlock>? source,
        string model,
        string? effort,
        int outputTokenCap,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTokenCap);
        if (effort is not null && effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Unsupported Claude CLI effort level.", nameof(effort));
        var initialUuid = Guid.NewGuid().ToString();
        var userMessage = ClaudeCliProtocol.UserMessage(prompt, null, source, uuid: initialUuid);
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(_executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Use the native claude.exe, not a shell or .cmd wrapper.");
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A turn is already running on this client.");

        try
        {
            // No project directory: nothing is discovered from or written to a workspace.
            var result = await RunProcessAsync(Path.GetTempPath(), userMessage, initialUuid, null, _ => { },
                    (_, _) => Task.FromResult(new ClaudeCliPermissionDecision(false, Message: "No tools are available.")),
                    cancellationToken, model, effort, null, null, null, oneShot: new(systemPrompt, outputTokenCap))
                .ConfigureAwait(false);
            if (result.IsError)
                throw new InvalidOperationException(result.Data.TryGetProperty("errors", out var errors)
                    ? errors.ToString() : ClaudeCliProtocol.String(result.Data, "result") ?? "Claude Code reported an error.");
            return ClaudeCliProtocol.String(result.Data, "result") ?? "";
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private sealed record OneShotOptions(string SystemPrompt, int OutputTokenCap);

    private async Task<ClaudeCliResult> RunProcessAsync(string directory, object userMessage, string initialUuid, string? sessionId,
        Action<ClaudeCliEvent> onEvent,
        Func<ClaudeCliPermissionRequest, CancellationToken, Task<ClaudeCliPermissionDecision>> onPermission,
        CancellationToken cancellationToken, string? model, string? effort, string? sessionName,
        Action<long>? onInputPayloadSent, ClaudeCliTurnInput? queuedInput, string? systemPrompt = null,
        OneShotOptions? oneShot = null, string? settings = null, McpLaunch? mcp = null,
        IReadOnlyList<string>? addDirectories = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stderrLifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        using var writeLock = new SemaphoreSlim(1, 1);
        using var inputLock = new SemaphoreSlim(1, 1);
        using var process = new Process { StartInfo = CreateStartInfo(directory, sessionId, model, effort, sessionName, systemPrompt, oneShot, settings, mcp, addDirectories) };
        var pending = new ConcurrentDictionary<string, CancellationTokenSource>();
        var handlers = new List<Task>();
        var stderr = new StringBuilder();
        Task stderrTask = Task.CompletedTask;
        var started = false;
        var initializeId = Guid.NewGuid().ToString();
        var initialized = false;
        var protocolComplete = false;
        ClaudeCliResult? result = null;
        Task inputTask = Task.CompletedTask;
        var outstanding = new HashSet<string>(StringComparer.Ordinal) { initialUuid };
        var submitted = new HashSet<string>(StringComparer.Ordinal) { initialUuid };
        var acknowledged = new HashSet<string>(StringComparer.Ordinal);
        var resultFrames = new HashSet<string>(StringComparer.Ordinal);
        var replayFrameLimits = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var frameLimit = FrameCharacterLimit;
        Func<int> getFrameLimit = () => Volatile.Read(ref frameLimit);

        async Task PumpInputAsync()
        {
            try
            {
                while (true)
                {
                    await queuedInput!.WaitAsync(token).ConfigureAwait(false);
                    await inputLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        // The signal only completes empty once the host has closed input.
                        var messages = queuedInput.Take();
                        if (messages.Count == 0) return;
                        foreach (var message in messages)
                        {
                            if (!Guid.TryParse(message.Uuid, out _) || !submitted.Add(message.Uuid))
                                throw new InvalidDataException("Queued Claude input requires a unique message UUID.");
                            outstanding.Add(message.Uuid);
                            await SendAsync(ClaudeCliProtocol.UserMessage(message.Text, sessionId, uuid: message.Uuid),
                                token, inputUuid: message.Uuid).ConfigureAwait(false);
                        }
                    }
                    finally { inputLock.Release(); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { lifetime.Cancel(); throw; }
        }

        async Task SendAsync(object message, CancellationToken ct, string? inputUuid = null)
        {
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(message);
                if (inputUuid is not null)
                {
                    // --replay-user-messages echoes the entire input, including images/history.
                    // Allow that payload plus bounded CLI envelope metadata, not arbitrary large output.
                    var replayLimit = (int)Math.Min(int.MaxValue, (long)json.Length + 64 * 1024);
                    replayFrameLimits[inputUuid] = replayLimit;
                    Volatile.Write(ref frameLimit, Math.Max(frameLimit, replayLimit));
                }
                await process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                // Only the user/seed JSON frame, not control traffic or Claude's private API request.
                // Exclude the JSONL line terminator, matching provider payload measurements.
                if (inputUuid is not null) onInputPayloadSent?.Invoke(Encoding.UTF8.GetByteCount(json));
            }
            finally { writeLock.Release(); }
        }

        async Task HandlePermissionAsync(ClaudeCliPermissionRequest request, CancellationTokenSource requestLifetime)
        {
            try
            {
                ClaudeCliPermissionDecision decision;
                try
                {
                    var requestToken = requestLifetime.Token;
                    // Some console hosts block before returning a Task. Cancellation must still
                    // release the subprocess; hosts should also honor the token to dismiss UI.
                    decision = await Task.Run(() => onPermission(request, requestToken), requestToken)
                        .WaitAsync(requestToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (requestLifetime.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    // Host errors must never accidentally approve a tool or leak callback details.
                    decision = new(false, Message: "Host interaction failed; tool denied.");
                }
                await SendAsync(ClaudeCliProtocol.PermissionResponse(request, decision), requestLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestLifetime.IsCancellationRequested) { }
            catch (Exception) { lifetime.Cancel(); throw; }
            finally
            {
                pending.TryRemove(request.RequestId, out _);
                requestLifetime.Dispose();
            }
        }

        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            stderrTask = DrainStderrAsync(process.StandardError, stderr, stderrLifetime.Token);
            await SendAsync(new { type = "control_request", request_id = initializeId,
                request = new { subtype = "initialize" } }, token).ConfigureAwait(false);

            // A wedged handshake should fail without waiting for the host's whole-turn deadline.
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(30));
            while (await ReadFrameAsync(process.StandardOutput, getFrameLimit, initialized ? token : handshake.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var data = document.RootElement;
                var type = ClaudeCliProtocol.String(data, "type");
                var replayUuid = type == "user" && data.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True
                    ? ClaudeCliProtocol.String(data, "uuid") : null;
                if (line.Length > FrameCharacterLimit && (replayUuid is null
                    || !replayFrameLimits.TryGetValue(replayUuid, out var replayLimit) || line.Length > replayLimit))
                    throw new InvalidDataException("CLI frame exceeds 1 MiB characters.");
                if (type == "control_response")
                {
                    var response = data.GetProperty("response");
                    if (ClaudeCliProtocol.String(response, "request_id") == initializeId && !initialized)
                    {
                        if (ClaudeCliProtocol.String(response, "subtype") != "success")
                            throw new InvalidDataException("Claude CLI initialization was rejected.");
                        initialized = true;
                        await SendAsync(userMessage, token, inputUuid: initialUuid).ConfigureAwait(false);
                        if (queuedInput is not null) inputTask = PumpInputAsync();
                    }
                    continue;
                }
                if (type == "control_request")
                {
                    var id = data.GetProperty("request_id").GetString()!;
                    var request = data.GetProperty("request");
                    if (ClaudeCliProtocol.String(request, "subtype") != "can_use_tool")
                    {
                        await SendAsync(new { type = "control_response", response = new { subtype = "error", request_id = id,
                            error = "Unsupported Claude Code control request." } }, token).ConfigureAwait(false);
                        continue;
                    }
                    var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                    if (!pending.TryAdd(id, requestLifetime))
                    {
                        requestLifetime.Dispose();
                        throw new InvalidDataException("Duplicate CLI control request ID.");
                    }
                    var permission = new ClaudeCliPermissionRequest(id, request.GetProperty("tool_name").GetString()!,
                        request.GetProperty("input").Clone(), ClaudeCliProtocol.String(request, "tool_use_id"));
                    // Do not block stdout while a person is answering: cancellations still arrive here.
                    handlers.Add(Task.Run(() => HandlePermissionAsync(permission, requestLifetime), CancellationToken.None));
                    continue;
                }
                if (type == "control_cancel_request")
                {
                    if (pending.TryGetValue(data.GetProperty("request_id").GetString()!, out var requestLifetime))
                    {
                        try { requestLifetime.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    continue;
                }

                // Consumers need acknowledgment metadata only. Do not clone/retain echoed base64 or history.
                if (replayUuid is not null)
                    data = JsonSerializer.SerializeToElement(new { type, uuid = replayUuid, isReplay = true,
                        session_id = ClaudeCliProtocol.String(data, "session_id") });
                var cliEvent = ClaudeCliProtocol.ParseEvent(data);
                if (sessionId is not null && cliEvent.SessionId is not null && cliEvent.SessionId != sessionId)
                    throw new InvalidDataException("CLI returned a different session instead of resuming the requested session.");
                if (replayUuid is not null)
                {
                    await inputLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // Unknown/user tool-result frames and duplicate acknowledgments cannot deliver host input.
                        if (!submitted.Contains(replayUuid) || !acknowledged.Add(replayUuid)) continue;
                        onEvent(cliEvent);
                    }
                    finally { inputLock.Release(); }
                    continue;
                }
                if (type == "result")
                {
                    result = new(cliEvent.SessionId ?? throw new InvalidDataException("CLI result has no session ID."),
                        data.TryGetProperty("is_error", out var isError) && isError.GetBoolean(), data.Clone());
                    await inputLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var resultKey = data.TryGetProperty("result_index", out var index)
                            ? index.GetRawText() : data.GetRawText();
                        if (!resultFrames.Add(resultKey)) continue;
                        var completed = new HashSet<string>(StringComparer.Ordinal);
                        if (ClaudeCliProtocol.String(data, "user_message_uuid") is { } id) completed.Add(id);
                        if (data.TryGetProperty("user_message_uuids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            foreach (var value in ids.EnumerateArray())
                                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } valueId) completed.Add(valueId);
                        // Keep old CLI/single-input behavior, but never guess which extra message a result handled.
                        if (completed.Count == 0 && submitted.Count == 1) completed.Add(initialUuid);
                        if (completed.Count == 0 && !result.IsError)
                            throw new InvalidDataException("Claude CLI did not identify the user messages completed by its result.");
                        if (completed.Any(id => !submitted.Contains(id)) && !result.IsError)
                            throw new InvalidDataException("Claude CLI completed an unknown user message.");
                        if (completed.Any(id => submitted.Contains(id) && id != initialUuid && !acknowledged.Contains(id)) && !result.IsError)
                            throw new InvalidDataException("Claude CLI completed queued input without replaying its acknowledgment.");
                        outstanding.ExceptWith(completed);
                        onEvent(cliEvent);
                        if (result.IsError || outstanding.Count == 0 && (queuedInput?.TryClose() ?? true))
                        {
                            protocolComplete = true;
                            break;
                        }
                    }
                    finally { inputLock.Release(); }
                    continue;
                }
                onEvent(cliEvent);
            }

            // No background Agent tools are available. Closing stdin after result
            // lets Claude flush its persisted session before exiting (do not kill on success).
            lifetime.Cancel();
            await inputTask.ConfigureAwait(false);
            await Task.WhenAll(handlers).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            stderrLifetime.Cancel();
            await stderrTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!protocolComplete || result is null || !result.IsError && (outstanding.Count > 0 || process.ExitCode != 0))
                throw new InvalidOperationException($"Claude CLI ended without a successful protocol completion (exit {process.ExitCode}). {stderr}");
            return result;
        }
        finally
        {
            lifetime.Cancel();
            queuedInput?.TryClose();
            stderrLifetime.Cancel();
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            try { await Task.WhenAll(handlers).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await inputTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
    }

    private ProcessStartInfo CreateStartInfo(string directory, string? sessionId, string? model, string? effort,
        string? sessionName, string? systemPrompt, OneShotOptions? oneShot, string? settings, McpLaunch? mcp,
        IReadOnlyList<string>? addDirectories)
    {
        var info = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = Path.GetFullPath(directory), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // CLI 2.1.284's experimental tool-continuation layout consumes all four cache slots.
        // Opt out so our retained-history marker fits alongside the CLI's three markers.
        // Apply on every launch, including resumes: a saved seed can still contain our marker.
        // This is process-local and currently suppresses readable thinking summaries as a tradeoff.
        info.Environment["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"] = "1";
        // Omit the CLI's volatile Git snapshot before retained history so repository changes
        // do not invalidate that cached prefix. This also removes its built-in Git guidance;
        // the default system prompt carries explicit Git safety instructions instead.
        info.Environment["CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS"] = "1";
        info.Environment["CLAUDE_CODE_DISABLE_ADVISOR_TOOL"] = "1";
        // Auto memory would add hidden context outside the app's context management.
        info.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        // Keep requesting summaries if the CLI supports them without experimental features.
        // Leave thinking mode/budget and effort under Claude's control.
        foreach (var argument in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                     "--verbose", "--include-partial-messages", "--permission-prompts", "host",
                     "--replay-user-messages",
                     "--thinking-display", "summarized",
                     "--permission-prompt-tool", "stdio", "--permission-mode", "manual",
                     "--strict-mcp-config", "--no-chrome", "--disable-slash-commands", "--prompt-suggestions", "false",
                     "--tools", oneShot is not null ? "" : ClaudeCliCodingPolicy.AvailableTools })
            info.ArgumentList.Add(argument);
        if (oneShot is not null)
        {
            // "" disables every built-in tool; the replacement system prompt drops Claude Code's agent prompt.
            // A disposable request needs no project instructions, settings, or customizations.
            foreach (var argument in new[] { "--restricted", "--safe-mode", "--system-prompt", oneShot.SystemPrompt,
                         "--no-session-persistence" })
                info.ArgumentList.Add(argument);
            info.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = oneShot.OutputTokenCap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            // No --restricted or --safe-mode: both stop CLAUDE.md discovery (verified with CLI 2.1.286), so the
            // user's settings files load too. --settings overrides them for hooks and auto-compact, and its ask rules
            // join theirs: a matching call reaches the host's approval dialog despite --allowed-tools (CLI 2.1.288).
            // These variables leak in when the app is started from a CLI session and also stop discovery.
            info.Environment.Remove("CLAUDE_CODE_SAFE_MODE");
            info.Environment.Remove("CLAUDE_CODE_DISABLE_CLAUDE_MDS");
            // Availability and automatic approval are separate. Questions still reach the host.
            // A selected MCP server's tools are approved by its tool-name prefix.
            foreach (var argument in new[] { "--allowed-tools", mcp is null ? ClaudeCliCodingPolicy.AutomaticTools
                             : string.Join(',', mcp.ServerNames.Select(ClaudeCliCodingPolicy.McpToolPrefix).Prepend(ClaudeCliCodingPolicy.AutomaticTools)),
                         "--disallowed-tools", mcp is null ? ClaudeCliCodingPolicy.DeniedTools : ClaudeCliCodingPolicy.DeniedNativeTools,
                         "--settings", settings ?? ClaudeCliCodingPolicy.Settings,
                         "--append-system-prompt", ClaudeCliCodingPolicy.AppendedSystemPrompt(systemPrompt!) })
                info.ArgumentList.Add(argument);
            // --strict-mcp-config limits the turn to these servers. Their tools load eagerly while experimental
            // betas are off, so --tools needn't name them (verified with CLI 2.1.288).
            if (mcp is not null)
            {
                info.ArgumentList.Add("--mcp-config");
                info.ArgumentList.Add(mcp.ConfigPath);
            }
            // The file tools already reach paths outside the working directory without --restricted (CLI 2.1.288);
            // --add-dir lists these as working directories so Claude knows about them.
            foreach (var addDirectory in addDirectories ?? [])
            {
                info.ArgumentList.Add("--add-dir");
                info.ArgumentList.Add(addDirectory);
            }
        }
        if (sessionId is not null) info.ArgumentList.Add($"--resume={sessionId}");
        if (model is not null) info.ArgumentList.Add($"--model={model}");
        if (effort is not null) info.ArgumentList.Add($"--effort={effort}");
        if (sessionName is not null) info.ArgumentList.Add($"--name={sessionName}");
        return info;
    }

    private static async Task<string?> ReadFrameAsync(StreamReader reader, Func<int> getFrameLimit, CancellationToken token)
    {
        // Bound even malformed/unterminated frames by submitted input sizes. Recheck at the
        // boundary because queued input can increase the permitted replay size during this read.
        var limit = FrameCharacterLimit;
        var text = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return text.ToString();
            if (text.Length == limit)
            {
                limit = getFrameLimit();
                if (text.Length >= limit) throw new InvalidDataException("CLI frame exceeds its bounded character limit.");
            }
            text.Append(character[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static async Task DrainStderrAsync(StreamReader reader, StringBuilder tail, CancellationToken token)
    {
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
