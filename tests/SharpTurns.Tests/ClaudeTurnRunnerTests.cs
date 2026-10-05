using System.Text.Json;
using SharpTurns.App.Services;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

/// <summary>Runs whole turns against a fake POSIX CLI script and checks what the runner saves.</summary>
public sealed class ClaudeTurnRunnerTests : IDisposable
{
    private const string Session = "700c7fa5-e552-450e-8712-3ebb74e2857c";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-runner-{Guid.NewGuid():N}");
    private readonly ConversationStore _store;

    public ClaudeTurnRunnerTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new ConversationStore(Path.Combine(_directory, "test.db"));
    }

    [Fact]
    public async Task ToolsQuestionsAndPermissionsAreSavedInDisplayOrder()
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + Lines(
            """{"type":"system","subtype":"init","session_id":"S","model":"claude-haiku-4-5-20251001"}""",
            """{"type":"stream_event","session_id":"S","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","session_id":"S","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Checking."}}}""",
            """{"type":"assistant","session_id":"S","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""",
            """{"type":"user","session_id":"S","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"a.txt"}]}}""",
            """{"type":"control_request","request_id":"ask-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","tool_use_id":"toolu_2","input":{"questions":[{"question":"Which color?","header":"Color","multiSelect":false,"options":[{"label":"Blue","description":"Calm"},{"label":"Red"}]}]}}}""")
            + "IFS= read -r reply\nprintf '%s' \"$reply\" > ask-reply.json\n" + Lines(
            """{"type":"control_request","request_id":"write-1","request":{"subtype":"can_use_tool","tool_name":"Write","tool_use_id":"toolu_3","input":{"file_path":".claude/settings.json","content":"{}"}}}""")
            + "IFS= read -r reply\nprintf '%s' \"$reply\" > write-reply.json\n" + Lines(
            """{"type":"stream_event","session_id":"S","event":{"type":"message_start","message":{"id":"m2","usage":{"input_tokens":10,"cache_read_input_tokens":900,"cache_creation_input_tokens":90,"output_tokens":1}}}}""",
            """{"type":"stream_event","session_id":"S","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","session_id":"S","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Done."}}}""",
            $$$"""{"type":"result","session_id":"{{{Session}}}","is_error":false,"result":"Done.","usage":{"input_tokens":20,"cache_read_input_tokens":1800,"cache_creation_input_tokens":180,"output_tokens":40}}""")
            + "cat > remaining-input.txt\n").Replace("\"S\"", $"\"{Session}\""));
        var (project, conversation) = await CreateConversationAsync();
        var writeStatuses = new List<string>();
        string? asked = null;
        var callbacks = Callbacks(
            tool => { if (tool.Name == "Write") writeStatuses.Add(tool.Status); },
            (question, _) =>
            {
                asked = question.Text;
                return Task.FromResult<string?>("Blue");
            },
            (_, _, _) => Task.FromResult(false));

        var result = await RunAsync(runner, project, conversation, callbacks);

        var turn = result.Turn;
        Assert.Equal(TurnStatus.Completed, turn.Status);
        Assert.Equal("Which color?", asked);
        Assert.Equal([TurnParts.Text, TurnParts.Text, TurnParts.Tool, TurnParts.Question, TurnParts.Tool, TurnParts.Text],
            turn.Parts.Select(p => p.PartType));
        Assert.Equal("Checking.", turn.Parts[1].Content);
        Assert.Equal(("Bash", "Completed", "a.txt"), (TurnParts.ReadTool(turn.Parts[2]).Name, TurnParts.ReadTool(turn.Parts[2]).Status,
            TurnParts.ReadTool(turn.Parts[2]).Result));
        Assert.Equal(new QuestionRecord("**Color**\n\nWhich color?\n\n- **Blue**: Calm\n- **Red**", "Blue"),
            TurnParts.ReadQuestion(turn.Parts[3]));
        Assert.Equal(("Write", "Denied by you", true), (TurnParts.ReadTool(turn.Parts[4]).Name, TurnParts.ReadTool(turn.Parts[4]).Status,
            TurnParts.ReadTool(turn.Parts[4]).IsError));
        Assert.Equal("Done.", turn.Parts[5].Content);
        Assert.Equal(["Waiting for your approval", "Denied by you"], writeStatuses);
        Assert.Equal(new TurnUsage(2000, 1800, 40, 1000, 1, 1000, 900), turn.Usage);
        Assert.Equal("claude-haiku-4-5-20251001", turn.Model);
        Assert.Empty(result.UndeliveredMessages);

        using var ask = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "ask-reply.json")));
        var answer = ask.RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("allow", answer.GetProperty("behavior").GetString());
        Assert.Equal("Blue", answer.GetProperty("updatedInput").GetProperty("answers").GetProperty("Which color?").GetString());
        using var write = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "write-reply.json")));
        Assert.Equal("deny", write.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
        var session = await _store.LoadClaudeCodeSessionAsync(conversation.Id);
        Assert.Equal((Session, false), (session!.SessionId, session.InFlight));
    }

    [Fact]
    public async Task EachToolKeepsTheCacheUsageOfTheRequestThatMadeIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + Lines(
            """{"type":"stream_event","session_id":"S","event":{"type":"message_start","message":{"id":"m1","usage":{"input_tokens":100,"cache_read_input_tokens":700,"cache_creation_input_tokens":200}}}}""",
            """{"type":"assistant","session_id":"S","message":{"id":"m1","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}},{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"/a.txt"}}]}}""",
            """{"type":"user","session_id":"S","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"a.txt"},{"type":"tool_result","tool_use_id":"toolu_2","content":"text"}]}}""",
            """{"type":"stream_event","session_id":"S","event":{"type":"message_start","message":{"id":"m2","usage":{"input_tokens":10,"cache_read_input_tokens":990}}}}""",
            """{"type":"assistant","session_id":"S","message":{"id":"m2","content":[{"type":"tool_use","id":"toolu_3","name":"Bash","input":{"command":"pwd"}}]}}""",
            """{"type":"user","session_id":"S","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_3","content":"/"}]}}""",
            $$"""{"type":"result","session_id":"{{Session}}","is_error":false,"result":"Done."}""")
            + "cat > remaining-input.txt\n").Replace("\"S\"", $"\"{Session}\""));
        var (project, conversation) = await CreateConversationAsync();

        var result = await RunAsync(runner, project, conversation, Callbacks());

        var tools = result.Turn.Parts.Where(p => p.PartType == TurnParts.Tool).Select(TurnParts.ReadTool).ToArray();
        Assert.Equal([("toolu_1", 1000L, 700L), ("toolu_2", 1000L, 700L), ("toolu_3", 1000L, 990L)],
            tools.Select(t => (t.Id, t.RequestInputTokens!.Value, t.RequestCachedTokens!.Value)));
        Assert.Equal($"Cache {70.0:0.0}%", new App.ViewModels.ToolItemViewModel(tools[0]).CacheUsageBadge);
        Assert.False(new App.ViewModels.ToolItemViewModel(tools[0] with { RequestInputTokens = null }).HasCacheUsage);
    }

    [Fact]
    public async Task QueuedMessagesAreSavedWhereTheCliAcceptedThem()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + """
            first=$(printf '%s' "$user" | sed -E 's/.*"uuid":"([^"]+)".*/\1/')
            IFS= read -r queued
            second=$(printf '%s' "$queued" | sed -E 's/.*"uuid":"([^"]+)".*/\1/')
            printf '{"type":"user","isReplay":true,"uuid":"%s","session_id":"S","message":{"role":"user","content":"also this"}}\n' "$second"
            printf '%s\n' '{"type":"stream_event","session_id":"S","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Both done."}}}'
            printf '{"type":"result","session_id":"S","is_error":false,"result":"Both done.","user_message_uuids":["%s","%s"]}\n' "$first" "$second"
            cat > remaining-input.txt

            """.Replace("\"S\"", $"\"{Session}\"")));
        var (project, conversation) = await CreateConversationAsync();
        var queue = new ClaudeCliInputQueue();
        Assert.True(queue.TryEnqueue("also this", out _));
        var delivered = new List<string>();

        var result = await RunAsync(runner, project, conversation, Callbacks(delivered: message => delivered.Add(message.Text)), queue);

        Assert.Equal(TurnStatus.Completed, result.Turn.Status);
        Assert.Equal([("user", "prompt"), ("user", "also this"), ("assistant", "Both done.")],
            result.Turn.Parts.Select(p => (p.Role, p.Content)));
        Assert.Equal(["also this"], delivered);
        Assert.Empty(result.UndeliveredMessages);
    }

    [Fact]
    public async Task MessagesTheCliNeverAcceptedAreReturned()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + Lines(
            $$"""{"type":"result","session_id":"{{Session}}","is_error":true,"result":"API error"}""") + "cat > remaining-input.txt\n"));
        var (project, conversation) = await CreateConversationAsync();
        var queue = new ClaudeCliInputQueue();
        Assert.True(queue.TryEnqueue("too late", out _));

        var result = await RunAsync(runner, project, conversation, Callbacks(), queue);

        Assert.Equal((TurnStatus.Failed, "API error"), (result.Turn.Status, result.Turn.ErrorMessage));
        Assert.Equal(["too late"], result.UndeliveredMessages);
        Assert.True((await _store.LoadClaudeCodeSessionAsync(conversation.Id))!.InFlight);
    }

    [Fact]
    public async Task TurnsRunInTheConversationsWorkspaceWithItsEnabledMcpServers()
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + """
            previous=
            for argument in "$@"; do
              if [ "$previous" = "--mcp-config" ]; then cp "$argument" mcp.json; fi
              if [ "$previous" = "--settings" ]; then printf '%s' "$argument" > settings.json; fi
              previous=$argument
            done
            """ + "\n" + Lines(
            """{"type":"control_request","request_id":"mcp-1","request":{"subtype":"can_use_tool","tool_name":"mcp__browser__navigate","tool_use_id":"toolu_1","input":{"url":"https://example.com"}}}""")
            + "IFS= read -r reply\nprintf '%s' \"$reply\" > selected-reply.json\n" + Lines(
            """{"type":"control_request","request_id":"mcp-2","request":{"subtype":"can_use_tool","tool_name":"mcp__db__query","tool_use_id":"toolu_2","input":{}}}""")
            + "IFS= read -r reply\nprintf '%s' \"$reply\" > disabled-reply.json\n" + Lines(
            $$$"""{"type":"result","session_id":"{{{Session}}}","is_error":false,"result":"Done."}""")
            + "cat > remaining-input.txt\n"));
        var (project, conversation) = await CreateConversationAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(_directory, "workspace")).FullName;
        var browser = await _store.CreateMcpServerAsync("browser", "Browser", null, """["npx", "browser-mcp"]""",
            """{"PORT": 9222, "MODE": "headless"}""", null, true);
        var db = await _store.CreateMcpServerAsync("db", "Database", null, """["db-mcp"]""", null, null, false);
        conversation = await _store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, workspace, false,
            null, null, [browser.Id, db.Id], []);
        await _store.SetSettingAsync(ClaudeTurnRunner.AskRulesSetting, "Bash(git push:*)\nmcp__db");
        var approvals = new List<string>();

        var result = await RunAsync(runner, project, conversation, Callbacks(approve: (tool, _, _) =>
        {
            approvals.Add(tool);
            return Task.FromResult(true);
        }));

        Assert.Equal(TurnStatus.Completed, result.Turn.Status);
        // The disabled server isn't started; an environment value that isn't a string passes as its JSON.
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "mcp.json")));
        var servers = config.RootElement.GetProperty("mcpServers");
        Assert.Equal(["browser"], servers.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("9222", "headless"), (servers.GetProperty("browser").GetProperty("env").GetProperty("PORT").GetString(),
            servers.GetProperty("browser").GetProperty("env").GetProperty("MODE").GetString()));
        // A started server's tool goes to the user like a native one the CLI asks about; a disabled server's is refused.
        Assert.Equal(["mcp__browser__navigate"], approvals);
        Assert.Contains("\"allow\"", await File.ReadAllTextAsync(Path.Combine(workspace, "selected-reply.json")));
        Assert.Contains("outside the enabled coding scope", await File.ReadAllTextAsync(Path.Combine(workspace, "disabled-reply.json")));
        // The saved ask rules replace the built-in ones.
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "settings.json")));
        Assert.Equal(["Bash(git push:*)", "mcp__db"], settings.RootElement.GetProperty("permissions").GetProperty("ask")
            .EnumerateArray().Select(rule => rule.GetString()));
        var state = (await _store.LoadClaudeCodeSessionAsync(conversation.Id))!;
        Assert.Equal(workspace, state.WorkingDirectory);
        Assert.Equal(ClaudeCliCodingPolicy.VersionFor("Host rules.", null, ["browser"]), state.PolicyVersion);
    }

    [Fact]
    public async Task ContextFilesAreSentToFreshSessionsAndAChangedFileStartsOne()
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake + "printf '%s\\n' \"$user\" >> user.jsonl\n" + Lines(
            $$$"""{"type":"result","session_id":"{{{Session}}}","is_error":false,"result":"Done."}""") + "cat > /dev/null\n"));
        var (project, conversation) = await CreateConversationAsync();
        var plan = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "docs")).FullName, "plan.md");
        await File.WriteAllTextAsync(plan, "Plan v1");
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.md"), "Off");
        conversation = await _store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, null, false, null, null, [],
        [
            new("docs/plan.md", ContextFileRoles.ActiveOperationalDocument, "Track progress", true, true, false),
            new("missing.md", ContextFileRoles.ReferenceSource, null, true, false, false),
            new("notes.md", ContextFileRoles.ReferenceSource, null, false, false, false),
        ]);

        var first = await RunAsync(runner, project, conversation, Callbacks());
        var unchanged = await RunAsync(runner, project, conversation, Callbacks());
        await File.WriteAllTextAsync(plan, "Plan v2");
        var changed = await RunAsync(runner, project, conversation, Callbacks());
        File.Delete(plan);
        var required = await RunAsync(runner, project, conversation, Callbacks());

        Assert.Equal([TurnStatus.Completed, TurnStatus.Completed, TurnStatus.Completed],
            new[] { first, unchanged, changed }.Select(r => r.Turn.Status));
        // An optional file that can't be read is left out and reported; a disabled one is skipped.
        Assert.Equal(["missing.md: The file doesn't exist."], first.OmittedContextFiles);
        var messages = (await File.ReadAllLinesAsync(Path.Combine(_directory, "user.jsonl")))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content")).ToArray();
        // The required file failed the fourth turn before the CLI started.
        Assert.Equal(3, messages.Length);
        var sent = messages[0].EnumerateArray().Select(b => b.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Contains("Path: docs/plan.md", sent[0]);
        Assert.Contains("User-provided purpose: Track progress", sent[0]);
        Assert.Contains("[BEGIN USER-CONFIGURED FILE CONTENT]\nPlan v1\n[END USER-CONFIGURED FILE CONTENT]", sent[0]);
        Assert.Equal("Current user request:\nprompt", sent[1]);
        // Unchanged files stay in the resumed session.
        Assert.Equal("prompt", messages[1].GetString());
        Assert.Contains(messages[2].EnumerateArray(), b => b.GetProperty("text").GetString()!.Contains("Plan v2"));
        Assert.Equal(TurnStatus.Failed, required.Turn.Status);
        Assert.StartsWith("A required context file couldn't be included. docs/plan.md: The file doesn't exist.",
            required.Turn.ErrorMessage);
    }

    [Fact]
    public async Task SavedFolderAccessAddsExistingFoldersAndReportsSkippedOnes()
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var runner = new ClaudeTurnRunner(_store, CreateFakeCli(Handshake
            + "printf '%s\\0' \"$@\" > args.bin\nprintf '%s\\n' \"$user\" >> user.jsonl\n" + Lines(
            $$"""{"type":"result","session_id":"{{Session}}","is_error":false,"result":"Done."}""") + "cat > /dev/null\n"));
        var (project, conversation) = await CreateConversationAsync();
        var shared = Directory.CreateDirectory(Path.Combine(_directory, "shared")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        var missing = Path.Combine(_directory, "missing");
        conversation = await _store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, null, false, null, null,
            [], [], $"{shared}\n{missing}\nrelative", other, ".env");

        var result = await RunAsync(runner, project, conversation, Callbacks());

        Assert.Equal(TurnStatus.Completed, result.Turn.Status);
        Assert.Equal([missing, "relative"], result.SkippedFolders);
        var args = (await File.ReadAllTextAsync(Path.Combine(_directory, "args.bin"))).Split('\0');
        Assert.Equal([shared, other], args.Where((_, i) => i > 0 && args[i - 1] == "--add-dir"));
        using var settings = JsonDocument.Parse(args[Array.IndexOf(args, "--settings") + 1]);
        var deny = settings.RootElement.GetProperty("permissions").GetProperty("deny").EnumerateArray()
            .Select(rule => rule.GetString()).ToArray();
        Assert.Contains($"Edit(/{shared}/**)", deny);
        Assert.DoesNotContain(deny, rule => rule!.Contains(other, StringComparison.Ordinal));
        Assert.Contains("Read(//**/*.env*)", deny);

        // Claude is told the access on a fresh session, and on a resumed one only when it changed.
        await RunAsync(runner, project, conversation, Callbacks());
        conversation = await _store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, null, false, null, null,
            [], [], other, null, ".env");
        await RunAsync(runner, project, conversation, Callbacks());
        conversation = await _store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, null, false, null, null,
            [], [], null, null, null);
        await RunAsync(runner, project, conversation, Callbacks());
        await RunAsync(runner, project, conversation, Callbacks());
        // A fresh session after access was cleared still gets the note, because the replayed history names the old access.
        var saved = await _store.LoadClaudeCodeSessionAsync(conversation.Id);
        await _store.SaveClaudeCodeSessionAsync(conversation.Id, saved! with { InFlight = true });
        await RunAsync(runner, project, conversation, Callbacks());
        var frames = (await File.ReadAllLinesAsync(Path.Combine(_directory, "user.jsonl")))
            .Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        Assert.Equal(["", Session, Session, Session, Session, ""], frames.Select(f => f.GetProperty("session_id").GetString()));
        var content = frames.Select(f => f.GetProperty("message").GetProperty("content")).ToArray();
        string[] Texts(JsonElement c) => c.EnumerateArray().Select(b => b.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(ClaudeCliCodingPolicy.FolderAccessNote(new([shared], [other], [".env"])), Texts(content[0])[0]);
        Assert.Equal("prompt", content[1].GetString());
        Assert.Equal([ClaudeCliCodingPolicy.FolderAccessNote(new([other], [], [".env"])), "Current user request:\nprompt"],
            Texts(content[2]));
        Assert.Equal([ClaudeCliCodingPolicy.FolderAccessNote(null), "Current user request:\nprompt"], Texts(content[3]));
        Assert.Equal("prompt", content[4].GetString());
        Assert.Equal([ClaudeCliCodingPolicy.FolderAccessNote(null), "Current user request:\nprompt"], Texts(content[5])[^2..]);
    }

    private async Task<(Project, Conversation)> CreateConversationAsync()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", _directory);
        return (project, await _store.CreateConversationAsync(project.Id, "Conversation"));
    }

    private static async Task<TurnRunResult> RunAsync(ClaudeTurnRunner runner, Project project, Conversation conversation,
        TurnCallbacks callbacks, ClaudeCliInputQueue? queue = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await runner.RunAsync(project, conversation, "prompt", [], "Host rules.", queue ?? new ClaudeCliInputQueue(),
            callbacks, deadline.Token);
    }

    private static TurnCallbacks Callbacks(Action<ToolCallRecord>? tool = null,
        Func<ClaudeCliQuestion, CancellationToken, Task<string?>>? ask = null,
        Func<string, string, CancellationToken, Task<bool>>? approve = null,
        Action<ClaudeCliUserMessage>? delivered = null) =>
        new(_ => { }, _ => { }, _ => { }, tool ?? (_ => { }), (_, _, _) => { }, delivered ?? (_ => { }), _ => { }, _ => { },
            ask ?? ((_, _) => Task.FromResult<string?>(null)), approve ?? ((_, _, _) => Task.FromResult(false)));

    private static string Lines(params string[] frames) =>
        string.Concat(frames.Select(frame => $"printf '%s\\n' '{frame}'\n"));

    private const string Handshake = """
        IFS= read -r init
        id=$(printf '%s' "$init" | sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{}}}\n' "$id"
        IFS= read -r user

        """;

    private string CreateFakeCli(string body)
    {
        var path = Path.Combine(_directory, "fake-claude");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
