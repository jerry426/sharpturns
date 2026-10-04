using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class BranchingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-branch-{Guid.NewGuid():N}");

    [Fact]
    public async Task ABranchCopiesAContiguousRunRenumberedAndLeavesTheSourceAlone()
    {
        var (store, source, turns) = await CreateAsync(5);
        await store.SetTurnHydratedAsync(turns[2].Id, false);
        await store.SetTurnSummaryAsync(turns[3].Id, "## Work Summary\nDid it.", "claude-sonnet-5-5");
        await store.SetConversationOutputStyleAsync(source.Id, "Concise");
        var server = await store.CreateMcpServerAsync("browser", "Browser", null, """["npx", "browser"]""", null, null, true);
        await store.UpdateConversationAsync(source.Id, "Conversation", source.ProjectId, _directory, true,
            "claude-haiku-4-5-20251001", "low", [server.Id],
            [new("docs/plan.md", ContextFileRoles.ActiveOperationalDocument, "The plan", true, true, false)]);
        await store.SaveClaudeCodeSessionAsync(source.Id, new(Guid.NewGuid().ToString(), _directory, "fingerprint", false));

        var branch = await store.BranchConversationAsync(source.Id, [turns[3].Id, turns[1].Id, turns[2].Id]);

        Assert.Equal((source.ProjectId, "Conversation", "claude-opus-5-5", "Concise", true),
            (branch.ProjectId, branch.Title, branch.Model, branch.OutputStyle, branch.AutoSummarize));
        // The workspace, summarizer, MCP servers, and context files carry over; protection doesn't.
        Assert.Equal((_directory, false), (branch.WorkingDirectory, branch.IsProtected));
        Assert.Equal(("claude-haiku-4-5-20251001", "low"), (branch.SummarizerModel, branch.SummarizerEffort));
        Assert.Equal(["browser"], (await store.ListConversationMcpServersAsync(branch.Id)).Select(s => s.Name));
        Assert.Equal(await store.ListContextFilesAsync(source.Id), await store.ListContextFilesAsync(branch.Id));
        Assert.Null(await store.LoadClaudeCodeSessionAsync(branch.Id));
        var copied = await store.LoadTurnsAsync(branch.Id);
        Assert.Equal([1, 2, 3], copied.Select(t => t.TurnNumber));
        Assert.Equal(["Prompt 2", "Prompt 3", "Prompt 4"], copied.Select(t => t.Parts[0].Content));
        Assert.Equal(["Reply 2", "Reply 3", "Reply 4"], copied.Select(t => t.Parts[1].Content));
        Assert.Equal([true, false, true], copied.Select(t => t.IsHydrated));
        Assert.Equal(("## Work Summary\nDid it.", "claude-sonnet-5-5"), (copied[2].Summary, copied[2].SummaryModel));
        Assert.Equal(turns[1].CreatedAt, copied[0].CreatedAt);
        Assert.Equal(5, (await store.LoadTurnsAsync(source.Id)).Count);
        Assert.Equal(2, (await store.ListConversationsAsync(source.ProjectId)).Count);

        // A gap, a turn from another conversation, or a running turn is refused, and nothing is created.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BranchConversationAsync(source.Id, [turns[0].Id, turns[2].Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BranchConversationAsync(source.Id, [copied[0].Id]));
        var running = await store.StartTurnAsync(source.Id, "Prompt 6");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BranchConversationAsync(source.Id, [turns[4].Id, running.Id]));
        Assert.Equal(2, (await store.ListConversationsAsync(source.ProjectId)).Count);
    }

    [Fact]
    public async Task EndpointsAndTheContextTabBranchIntoANewConversationThatOpens()
    {
        var (store, source, turns) = await CreateAsync(6);
        await store.SetTurnHydratedAsync(turns[3].Id, false);
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store));
        await main.InitializeAsync();
        await WaitAsync(() => main.Conversations.Count == 1);
        var conversation = main.CurrentConversation!;
        await WaitAsync(() => conversation.Turns.Count == 6);
        BranchConfirmation? asked = null;
        var confirm = false;
        conversation.ConfirmBranchAsync = c => Task.FromResult((asked = c) is not null && confirm);

        // Two endpoints ask first; declining clears them.
        var third = conversation.Turns[2];
        await conversation.ToggleBranchEndpointCommand.ExecuteAsync(third);
        Assert.True(third.IsBranchEndpoint);
        Assert.Equal(("✓ Branch Endpoint Selected", "#FFCC00"), (third.BranchEndpointLabel, third.CardBorderBrush));
        await conversation.ToggleBranchEndpointCommand.ExecuteAsync(conversation.Turns[4]);
        Assert.Equal("Turn 3 → Turn 5 · 3 turn(s) included", asked!.DetailText);
        Assert.Equal("Branch canceled.", conversation.Status);
        Assert.DoesNotContain(conversation.Turns, t => t.IsBranchEndpoint);

        // Confirmed, the range includes the hidden turn between, and the branch opens with its turns renumbered.
        confirm = true;
        await conversation.ToggleBranchEndpointCommand.ExecuteAsync(conversation.Turns[4]);
        await conversation.ToggleBranchEndpointCommand.ExecuteAsync(third);
        Assert.Equal("Branched turns 3–5 into a new conversation.", conversation.Status);
        Assert.Equal(2, main.Conversations.Count);
        var branch = main.CurrentConversation!;
        Assert.NotSame(conversation, branch);
        Assert.Equal(main.Conversations[0], main.SelectedConversation);
        Assert.Equal("Branched from turns 3–5 (now 1–3).", branch.Status);
        await WaitAsync(() => branch.Turns.Count == 3);
        Assert.Equal([true, false, true], branch.Turns.Select(t => t.IsHydrated));

        // Branch Range needs a contiguous selection; the Show picker left turn 4 out of the list.
        main.SelectedConversation = main.Conversations[1];
        conversation.SelectContextTurn(conversation.ShownTurns[1], range: false);
        conversation.SelectContextTurn(conversation.ShownTurns[3], range: true);
        Assert.Equal("Branch Range (3)", conversation.BranchRangeLabel);
        Assert.False(conversation.BranchThroughSelectedTurnCommand.CanExecute(null));
        await conversation.BranchSelectedRangeCommand.ExecuteAsync(null);
        Assert.Equal("Branch Range copies every turn from turn 2 to turn 5, so select turn 4 too. Set Show to All Turns to list it.",
            conversation.Status);
        main.Display.TurnFilterIndex = ConversationDisplayViewModel.AllTurns;
        conversation.SelectContextTurn(conversation.Turns[3], range: false);
        await conversation.BranchSelectedRangeCommand.ExecuteAsync(null);
        Assert.Equal("Turns 2, 3, 4, 5 · ids " + string.Join(", ", turns.Skip(1).Take(4).Select(t => t.Id)), asked.DetailText);
        Assert.Equal("Branched turns 2–5 into a new conversation.", conversation.Status);
        Assert.Equal("0 selected", conversation.SelectedTurnsLabel);

        // Branch Through copies every turn from the first through the one selected.
        main.SelectedConversation = main.Conversations.Single(c => c.Id == source.Id);
        conversation.SelectContextTurn(conversation.Turns[1], range: false);
        await conversation.BranchThroughSelectedTurnCommand.ExecuteAsync(null);
        Assert.Equal($"Turn 2 · id {turns[1].Id}", asked.DetailText);
        Assert.Equal(4, main.Conversations.Count);
        await WaitAsync(() => main.CurrentConversation!.Turns.Count == 2);
        Assert.Equal(["Prompt 1", "Prompt 2"], (await store.LoadTurnsAsync(main.Conversations[0].Id)).Select(t => t.Parts[0].Content));
        Assert.Equal(6, (await store.LoadTurnsAsync(source.Id)).Count);
    }

    private async Task<(ConversationStore Store, Conversation Source, IReadOnlyList<ConversationTurn> Turns)> CreateAsync(int count)
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var source = await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        for (var i = 1; i <= count; i++)
        {
            var turn = await store.StartTurnAsync(source.Id, $"Prompt {i}");
            await store.FinishTurnAsync(turn.Id, TurnStatus.Completed, null, [new(2, "assistant", "text", $"Reply {i}")]);
        }
        return (store, source, await store.LoadTurnsAsync(source.Id));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
