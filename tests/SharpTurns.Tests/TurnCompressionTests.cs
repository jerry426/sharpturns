using System.Text.Json;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class TurnCompressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-compression-{Guid.NewGuid():N}");

    public TurnCompressionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void AnalyzeKeepsTheFinalResponseVerbatimAndSummarizesTheRest()
    {
        var turn = ToolTurn(TurnStatus.Completed);

        var source = TurnCompression.Analyze(turn, out var reason);

        Assert.Null(reason);
        Assert.Equal((TurnCompression.FinalResponseHeading, "All tests pass.", true),
            (source!.ResponseHeading, source.Response, source.HasWork));
        using var json = JsonDocument.Parse(source.SummarizerSource[(source.SummarizerSource.IndexOf('\n') + 1)..]);
        Assert.Equal(["user", "assistant", "assistant", "tool"],
            json.RootElement.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.DoesNotContain("All tests pass.", source.SummarizerSource);
        Assert.Contains("dotnet test", source.SummarizerSource);
    }

    [Fact]
    public void AnalyzeExplainsTurnsItCantCompress()
    {
        var turn = ToolTurn(TurnStatus.Completed);

        Assert.Null(TurnCompression.Analyze(turn with { Status = TurnStatus.Running }, out var running));
        Assert.Equal("It's still running.", running);
        Assert.Null(TurnCompression.Analyze(turn with { Summary = "## Work Summary" }, out var compressed));
        Assert.Equal("It's already compressed.", compressed);
        Assert.Null(TurnCompression.Analyze(turn with { Parts = turn.Parts.Take(4).ToArray() }, out var endsWithTool));
        Assert.Equal("It doesn't end with a response from Claude.", endsWithTool);
        Assert.Null(TurnCompression.Analyze(turn with { Parts = turn.Parts.Take(1).ToArray() }, out var noResponse));
        Assert.Equal("It has no response from Claude to keep.", noResponse);
        // A stopped turn keeps its last text as a partial response, even when a tool call followed it.
        var stopped = TurnCompression.Analyze(turn with { Status = TurnStatus.Stopped, Parts = turn.Parts.Take(4).ToArray() }, out _);
        Assert.Equal((TurnCompression.PartialResponseHeading, "Running the tests."), (stopped!.ResponseHeading, stopped.Response));
    }

    [Theory]
    [InlineData("- Ran the tests.", "## Work Summary\n\n- Ran the tests.")]
    [InlineData("# Summary\n\n- Ran the tests.", "## Work Summary\n\n- Ran the tests.")]
    [InlineData("Here it is:\n## Work Summary\n- Ran the tests.", "## Work Summary\n- Ran the tests.")]
    public void WorkSummariesGetExactlyOneHeading(string generated, string expected) =>
        Assert.Equal(expected, TurnCompression.NormalizeWorkSummary(generated));

    [Theory]
    [InlineData("")]
    [InlineData("## Work Summary\n- a\n## Work Summary\n- b")]
    [InlineData("## Work Summary\n- a\n\n## Final Assistant Response — Verbatim\n\nmade up")]
    [InlineData("## Work Summary\n- a\n\n## Request\n\nFix the build.")]
    public void UnusableWorkSummariesAreRejected(string generated) =>
        Assert.Throws<InvalidOperationException>(() => TurnCompression.NormalizeWorkSummary(generated));

    // The conversation's own summarizer model and effort replace the default's.
    [Theory]
    [InlineData(null, null, "claude-sonnet-5-5", "high")]
    [InlineData("claude-haiku-4-5-20251001", "low", "claude-haiku-4-5-20251001", "low")]
    public async Task SummarizerSavesTheWorkSummaryAndVerbatimResponse(string? conversationModel, string? conversationEffort,
        string model, string effort)
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        var store = await CreateStoreAsync();
        var turn = await SaveTurnAsync(store, ToolTurn(TurnStatus.Completed));
        await store.SetSettingAsync(TurnSummarizer.EffortSetting, "high");
        var conversation = (await store.GetConversationAsync(turn.ConversationId))!;
        await store.UpdateConversationAsync(conversation.Id, conversation.Title, conversation.ProjectId, null, false,
            conversationModel, conversationEffort, [], []);
        var summarizer = new TurnSummarizer(store, CreateFakeCli("""
            printf '%s\n' "$@" > args.txt
            IFS= read -r init
            id=$(printf '%s' "$init" | sed -E 's/.*"request_id":"([^"]+)".*/\1/')
            printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{}}}\n' "$id"
            IFS= read -r user
            printf '%s' "$user" > user.json
            printf '%s\n' '{"type":"result","session_id":"700c7fa5-e552-450e-8712-3ebb74e2857c","is_error":false,"result":"Summary\n\n- Ran dotnet test."}'
            cat > /dev/null

            """));

        var compressed = await summarizer.CompressAsync(turn);

        Assert.Equal("## Work Summary\n\n- Ran dotnet test.\n\n## Final Assistant Response — Verbatim\n\nAll tests pass.", compressed.Summary);
        Assert.Equal(model, compressed.SummaryModel);
        var saved = Assert.Single(await store.LoadTurnsAsync(turn.ConversationId));
        Assert.Equal((compressed.Summary, model), (saved.Summary, saved.SummaryModel));
        var args = await File.ReadAllLinesAsync(Path.Combine(_directory, "args.txt"));
        Assert.Contains($"--model={model}", args);
        Assert.Contains($"--effort={effort}", args);
        var user = await File.ReadAllTextAsync(Path.Combine(_directory, "user.json"));
        Assert.Contains("dotnet test", user);
        Assert.Contains("HYBRID COMPRESSION INSTRUCTION", user);
        Assert.DoesNotContain("All tests pass.", user);
    }

    [Fact]
    public async Task ATurnThatWouldNotGetSmallerIsLeftAsIs()
    {
        var store = await CreateStoreAsync();
        var turn = await SaveTurnAsync(store, Turn(TurnStatus.Completed, [new(1, "user", "text", "hi"), new(2, "assistant", "text", "hello")]));

        // Nothing to summarize, so no CLI is started.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new TurnSummarizer(store, Path.Combine(_directory, "missing")).CompressAsync(turn));

        Assert.StartsWith("Compressing it wouldn't make it smaller", error.Message);
        Assert.Null(Assert.Single(await store.LoadTurnsAsync(turn.ConversationId)).Summary);
    }

    [Fact]
    public void CompressedCardsShowTheUserInputsAndSummaryUntilViewedInFull()
    {
        var full = ToolTurn(TurnStatus.Completed);
        var turn = new TurnViewModel(full);
        Assert.Equal(3, turn.Items.Count); // Thinking isn't shown.

        turn.ApplyRecord(full with
        {
            Summary = "## Work Summary\n\n- Ran dotnet test.\n\n## Final Assistant Response — Verbatim\n\nAll tests pass.",
            SummaryModel = "sonnet",
        });

        var summary = Assert.IsType<TextItemViewModel>(Assert.Single(turn.Items));
        Assert.True(summary.IsSummary);
        Assert.StartsWith("## Work Summary (1 tool call, ", summary.Text);
        Assert.Matches(@"^👁️ View Full Turn Content \(\d+\.\dx replay reduction\)$", turn.ViewFullContentLabel);
        Assert.Equal("Compressed · summary by sonnet", turn.CompressedLabel);
        Assert.True(turn.HasViewFullContentButton);

        turn.ViewFullContentCommand.Execute(null);
        Assert.Equal(3, turn.Items.Count);
        Assert.True(turn.IsViewingFullCompressedContent);

        turn.RestoreCompressedViewCommand.Execute(null);
        Assert.Single(turn.Items);
        turn.ApplyRecord(full);
        Assert.Equal((3, "Compress"), (turn.Items.Count, turn.CompressionActionLabel));
    }

    // A completed turn: prompt, text, a tool call with a long result, and the final response.
    private static ConversationTurn ToolTurn(TurnStatus status) => Turn(status,
    [
        new(1, "user", "text", "Run the tests."),
        new(2, "assistant", "text", "Running the tests."),
        new(3, "assistant", "thinking", "private"),
        TurnParts.FromTool(4, new("toolu_1", "Bash", """{"command":"dotnet test"}""", new string('x', 2000), "Completed", false)),
        new(5, "assistant", "text", "All tests pass."),
    ]);

    private static ConversationTurn Turn(TurnStatus status, TurnPart[] parts) =>
        new(1, 1, 1, status, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, parts);

    private async Task<ConversationStore> CreateStoreAsync()
    {
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task<ConversationTurn> SaveTurnAsync(ConversationStore store, ConversationTurn turn)
    {
        var project = await store.CreateProjectAsync("Project", "/work");
        var conversation = await store.CreateConversationAsync(project.Id, "Conversation");
        var started = await store.StartTurnAsync(conversation.Id, turn.Parts[0].Content);
        await store.FinishTurnAsync(started.Id, turn.Status, null, turn.Parts.Skip(1).ToArray());
        return Assert.Single(await store.LoadTurnsAsync(conversation.Id));
    }

    [Fact]
    public void TheReplayRecordSaysHowTheTurnEnded()
    {
        // The response keeps a heading of its own; only the first boundary splits the summary.
        var (record, response) = TurnCompression.ReplayPresentation(4,
            "## Work Summary\n\n- Stopped.\n\n## Partial Assistant Response — Verbatim\n\nHalf\n\n## Final Assistant Response — Verbatim\n\nquoted");
        Assert.StartsWith("[SHARPTURNS RECORD — COMPRESSED TURN 4]\n", record);
        Assert.Contains("the assistant's partial response, verbatim; the turn did not complete.\n\n## Work Summary\n\n- Stopped.", record);
        Assert.Equal("Half\n\n## Final Assistant Response — Verbatim\n\nquoted", response);

        (record, response) = TurnCompression.ReplayPresentation(4, "Legacy summary");
        Assert.EndsWith("the work summary covers the whole turn.\n\nLegacy summary", record);
        Assert.Null(response);
    }

    private string CreateFakeCli(string body)
    {
        var path = Path.Combine(_directory, "fake-claude");
        File.WriteAllText(path, "#!/bin/sh\ncd " + _directory + "\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
