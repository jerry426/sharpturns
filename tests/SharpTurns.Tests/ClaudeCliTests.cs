using System.Diagnostics;
using System.Text.Json;
using SharpTurns.ClaudeCli;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ClaudeCliTests : IDisposable
{
    private const string Session = "700c7fa5-e552-450e-8712-3ebb74e2857c";
    private const string SystemPrompt = "Host rules.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-cli-{Guid.NewGuid():N}");

    public ClaudeCliTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void TextDeltaSurvivesDocumentDisposal()
    {
        ClaudeCliEvent parsed;
        using (var document = JsonDocument.Parse("""
            {"type":"stream_event","session_id":"session","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}}
            """))
            parsed = ClaudeCliProtocol.ParseEvent(document.RootElement);
        Assert.Equal("hello", parsed.Text);
        Assert.Equal("session", parsed.SessionId);
        Assert.Equal("stream_event", parsed.Data.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"duplicate"}]}}""")]
    [InlineData("""{"type":"result","result":"duplicate"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"private"}}}""")]
    [InlineData("""{"type":"stream_event","parent_tool_use_id":"child","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"child"}}}""")]
    public void OnlyTopLevelTextDeltasAreRendered(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ClaudeCliProtocol.ParseEvent(document.RootElement).Text);
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"duplicate","signature":"opaque"}]}}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"signature_delta","signature":"opaque"}}}""")]
    [InlineData("""{"type":"stream_event","parent_tool_use_id":"child","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"child"}}}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"answer"}}}""")]
    public void ThinkingProjectionIgnoresEchoesOpaqueBlocksSubagentsAndOtherDeltas(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ClaudeCliProtocol.ParseEvent(document.RootElement).Thinking);
    }

    [Fact]
    public void ThinkingDeltaIsSeparateFromText()
    {
        using var document = JsonDocument.Parse("""
            {"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Reasoning summary."}}}
            """);
        var parsed = ClaudeCliProtocol.ParseEvent(document.RootElement);
        Assert.Equal("Reasoning summary.", parsed.Thinking);
        Assert.Null(parsed.Text);
    }

    [Fact]
    public void PermissionResponsePreservesQuestionsAndAddsAnswers()
    {
        var input = JsonSerializer.SerializeToElement(new { questions = new[] { new { question = "Marker?" } } });
        var updated = JsonSerializer.SerializeToElement(new { questions = input.GetProperty("questions"),
            answers = new Dictionary<string, string> { ["Marker?"] = "Indigo" } });
        var request = new ClaudeCliPermissionRequest("request-1", "AskUserQuestion", input);
        var response = JsonSerializer.SerializeToElement(ClaudeCliProtocol.PermissionResponse(request, new(true, updated)))
            .GetProperty("response");
        Assert.Equal("request-1", response.GetProperty("request_id").GetString());
        var decision = response.GetProperty("response");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.Equal("Indigo", decision.GetProperty("updatedInput").GetProperty("answers").GetProperty("Marker?").GetString());
    }

    [Fact]
    public void HistoryBlocksKeepTheirTextWhenRequestChangesOrHistoryGrows()
    {
        var history = Enumerable.Range(1, 30).Select(n => $"Retained turn {n}: café 🟢\n\"quoted\"").ToArray();
        JsonElement Content(string prompt, IReadOnlyList<ClaudeCliHistoryBlock> blocks) =>
            JsonSerializer.SerializeToElement(ClaudeCliProtocol.UserMessage(prompt, null, blocks))
                .GetProperty("message").GetProperty("content");
        var blocks = history.Select(text => new ClaudeCliHistoryBlock(text)).ToArray();
        var first = Content("request A", blocks);
        var changed = Content("request B", blocks);
        var grown = Content("request C", [.. blocks, new("new turn")]);
        Assert.Equal(31, first.GetArrayLength());
        for (var i = 0; i < history.Length; i++)
        {
            Assert.Equal(history[i], first[i].GetProperty("text").GetString());
            Assert.Equal(first[i].GetRawText(), changed[i].GetRawText());
            Assert.Equal(history[i], grown[i].GetProperty("text").GetString());
        }
        foreach (var content in new[] { first, changed, grown })
        {
            // Exactly one cache marker, on the last history block.
            var marked = Assert.Single(content.EnumerateArray(), b => b.TryGetProperty("cache_control", out _));
            Assert.Equal(content[content.GetArrayLength() - 2].GetRawText(), marked.GetRawText());
            Assert.Equal("1h", marked.GetProperty("cache_control").GetProperty("ttl").GetString());
        }
        Assert.Equal("Current user request:\nrequest B", changed[30].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Session)]
    public void NoRetainedHistoryUsesThePlainTextFrame(string? session)
    {
        const string prompt = "Only the new request.\nNo history.";
        var frame = JsonSerializer.SerializeToElement(ClaudeCliProtocol.UserMessage(prompt, session));
        Assert.Equal(prompt, frame.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(session ?? "", frame.GetProperty("session_id").GetString());
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "  ")]
    [InlineData(Session, "retained history")]
    public async Task InvalidHistoryIsRejectedBeforeLaunching(string? session, string block)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new ClaudeCliClient("not-an-executable").RunTurnAsync(
            _directory, "prompt", session, _ => { }, Deny, SystemPrompt, retainedHistory: [new(block)]));
    }

    [Theory]
    [InlineData("", null, SystemPrompt)]
    [InlineData("sonnet", "unsupported", SystemPrompt)]
    [InlineData(null, null, " ")]
    public async Task InvalidInvocationOptionsAreRejectedBeforeLaunch(string? model, string? effort, string systemPrompt)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new ClaudeCliClient("not-an-executable").RunTurnAsync(
            _directory, "prompt", null, _ => { }, Deny, systemPrompt, model: model, effort: effort));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ProcessRoundTripRelaysPermissionAndClosesInput(bool allow, bool callbackThrows)
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var executable = CreateFakeCli(Handshake + Permission + """
            IFS= read -r reply
            printf '%s' "$reply" > reply.json
            printf '%s\n' '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}}'
            printf '%s\n' '{"type":"result","session_id":"700c7fa5-e552-450e-8712-3ebb74e2857c","is_error":false,"result":"hello"}'
            cat > remaining-input.txt
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new List<string>();
        const string prompt = "new prompt: café 🟢\n\"quoted\"";
        var result = await new ClaudeCliClient(executable).RunTurnAsync(_directory, prompt, Session,
            e => { if (e.Text is not null) text.Add(e.Text); },
            (request, _) =>
            {
                Assert.Equal("Read", request.ToolName);
                if (callbackThrows) throw new InvalidOperationException("private host error");
                return Task.FromResult(new ClaudeCliPermissionDecision(allow));
            }, SystemPrompt, deadline.Token);
        Assert.False(result.IsError);
        Assert.Equal(Session, result.SessionId);
        Assert.Equal(["hello"], text);
        using var reply = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "reply.json")));
        var response = reply.RootElement.GetProperty("response");
        Assert.Equal("read-1", response.GetProperty("request_id").GetString());
        Assert.Equal(allow ? "allow" : "deny", response.GetProperty("response").GetProperty("behavior").GetString());
        Assert.DoesNotContain("private host error", response.GetRawText());
        Assert.Equal("", await File.ReadAllTextAsync(Path.Combine(_directory, "remaining-input.txt")));
        using var sent = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "user.json")));
        Assert.Equal(prompt, sent.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task TurnLaunchKeepsClaudeMdDiscoveryAndPassesTheCodingPolicy()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = CreateFakeCli(Handshake + """
            printf '%s\0' "$@" > args.bin
            printf '%s|%s|%s|%s|%s' "${CLAUDE_CODE_SAFE_MODE-unset}" "${CLAUDE_CODE_DISABLE_CLAUDE_MDS-unset}" \
              "$CLAUDE_CODE_DISABLE_AUTO_MEMORY" "$CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS" "$CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS" > env.txt
            printf '%s\n' '{"type":"result","session_id":"700c7fa5-e552-450e-8712-3ebb74e2857c","is_error":false}'
            cat > remaining-input.txt
            """);
        // A parent CLI session sets these; they must not reach the turn's CLI.
        var inherited = new[] { "CLAUDE_CODE_SAFE_MODE", "CLAUDE_CODE_DISABLE_CLAUDE_MDS" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in inherited.Keys) Environment.SetEnvironmentVariable(name, "1");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await new ClaudeCliClient(executable).RunTurnAsync(_directory, "prompt", Session, _ => { }, Deny,
                SystemPrompt, deadline.Token, model: "sonnet", effort: "high", sessionName: "My conversation");
        }
        finally
        {
            foreach (var (name, value) in inherited) Environment.SetEnvironmentVariable(name, value);
        }
        var args = (await File.ReadAllTextAsync(Path.Combine(_directory, "args.bin"))).Split('\0');
        string Value(string name) => args[Array.IndexOf(args, name) + 1];
        Assert.Equal(ClaudeCliCodingPolicy.AvailableTools, Value("--tools"));
        Assert.Equal(ClaudeCliCodingPolicy.AutomaticTools, Value("--allowed-tools"));
        Assert.DoesNotContain("AskUserQuestion", Value("--allowed-tools"));
        Assert.Equal(ClaudeCliCodingPolicy.DeniedTools, Value("--disallowed-tools"));
        Assert.Equal("manual", Value("--permission-mode"));
        Assert.Equal("host", Value("--permission-prompts"));
        Assert.Equal("summarized", Value("--thinking-display"));
        Assert.Equal(SystemPrompt, Value("--append-system-prompt"));
        using var settings = JsonDocument.Parse(Value("--settings"));
        Assert.True(settings.RootElement.GetProperty("disableAllHooks").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("autoCompactEnabled").GetBoolean());
        Assert.Empty(settings.RootElement.GetProperty("fallbackModel").EnumerateArray());
        Assert.Contains($"--resume={Session}", args);
        Assert.Contains("--model=sonnet", args);
        Assert.Contains("--effort=high", args);
        Assert.Contains("--name=My conversation", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--disable-slash-commands", args);
        Assert.Contains("--include-partial-messages", args);
        // Either flag would stop the CLI from discovering the project's CLAUDE.md.
        Assert.DoesNotContain("--restricted", args);
        Assert.DoesNotContain("--safe-mode", args);
        Assert.DoesNotContain(args, a => a.Contains("skip-permissions") || a.Contains("bypassPermissions"));
        Assert.Equal("unset|unset|1|1|1", await File.ReadAllTextAsync(Path.Combine(_directory, "env.txt")));
    }

    [Fact]
    public async Task OneShotDisablesToolsCustomizationsAndSessionPersistence()
    {
        if (OperatingSystem.IsWindows()) return;
        // One-shot runs outside any project directory, so the fake CLI records into the test directory explicitly.
        var executable = CreateFakeCli($"cd '{_directory}'\n" + Handshake + """
            printf '%s\n' "$@" > args.txt
            printf '%s' "$CLAUDE_CODE_MAX_OUTPUT_TOKENS" > cap.txt
            printf '%s\n' '{"type":"result","session_id":"700c7fa5-e552-450e-8712-3ebb74e2857c","is_error":false,"result":"## Work Summary\n\n- Done."}'
            cat > remaining-input.txt
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = await new ClaudeCliClient(executable).RunOneShotAsync("system rules", "summarize now",
            [new("source json")], "sonnet", "high", 8_192, deadline.Token);
        Assert.Equal("## Work Summary\n\n- Done.", text);
        var args = await File.ReadAllLinesAsync(Path.Combine(_directory, "args.txt"));
        Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]);
        Assert.Equal("system rules", args[Array.IndexOf(args, "--system-prompt") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--restricted", args);
        Assert.Contains("--safe-mode", args);
        Assert.Contains("--model=sonnet", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--resume") || a.StartsWith("--name") || a == "--append-system-prompt");
        Assert.Equal("8192", await File.ReadAllTextAsync(Path.Combine(_directory, "cap.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileAwaitingHumanKillsProcess(bool blockingHost)
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = CreateFakeCli(Handshake + Permission + "IFS= read -r reply\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, canceled.Token);
        using var releaseHost = new ManualResetEventSlim();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeCliClient(executable).RunTurnAsync(
                _directory, "prompt", null, _ => { }, async (_, token) =>
                {
                    canceled.Cancel();
                    if (blockingHost) releaseHost.Wait(TimeSpan.FromSeconds(10));
                    await Task.Delay(Timeout.Infinite, token);
                    return new(false);
                }, SystemPrompt, linked.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { releaseHost.Set(); }
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "pid")));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Theory]
    [InlineData("printf '%s\\n' 'not-json'", typeof(JsonException))]
    [InlineData("exit 7", typeof(InvalidOperationException))]
    [InlineData("printf '%s\\n' '{\"type\":\"result\",\"session_id\":\"wrong-session\",\"is_error\":false}'", typeof(InvalidDataException))]
    public async Task InvalidOutputOrExitDoesNotBecomeSuccess(string body, Type exceptionType)
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = CreateFakeCli(Handshake + body + "\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Record.ExceptionAsync(() => new ClaudeCliClient(executable).RunTurnAsync(
            _directory, "prompt", Session, _ => { }, Deny, SystemPrompt, deadline.Token));
        Assert.NotNull(error);
        Assert.True(exceptionType.IsInstanceOfType(error), $"Expected {exceptionType.Name}, got {error.GetType().Name}: {error.Message}");
    }

    [Fact]
    public void PolicyVersionChangesWithTheSystemPrompt()
    {
        var version = ClaudeCliCodingPolicy.VersionFor(ClaudeCliCodingPolicy.DefaultSystemPrompt);
        Assert.Equal(version, ClaudeCliCodingPolicy.VersionFor(ClaudeCliCodingPolicy.DefaultSystemPrompt));
        Assert.NotEqual(version, ClaudeCliCodingPolicy.VersionFor(ClaudeCliCodingPolicy.DefaultSystemPrompt + " Be brief."));
    }

    private static Task<ClaudeCliPermissionDecision> Deny(ClaudeCliPermissionRequest request, CancellationToken token) =>
        Task.FromResult(new ClaudeCliPermissionDecision(false));

    private const string Handshake = """
        IFS= read -r init
        id=$(printf '%s' "$init" | sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{}}}\n' "$id"
        IFS= read -r user
        printf '%s' "$user" > user.json

        """;

    private const string Permission = """
        printf '%s\n' '{"type":"control_request","request_id":"read-1","request":{"subtype":"can_use_tool","tool_name":"Read","input":{"file_path":"example.txt"}}}'

        """;

    private string CreateFakeCli(string body)
    {
        var path = Path.Combine(_directory, "fake-claude");
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s' \"$$\" > pid\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
