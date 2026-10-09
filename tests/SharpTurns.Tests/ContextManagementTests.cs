using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ContextManagementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-context-{Guid.NewGuid():N}");

    [Fact]
    public async Task SmartCleanupAndTheBulkActionsChangeTheSavedTurns()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var saved = await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        for (var i = 1; i <= 12; i++)
        {
            var turn = await store.StartTurnAsync(saved.Id, $"Prompt {i}");
            await store.FinishTurnAsync(turn.Id, TurnStatus.Completed, null, [new(2, "assistant", "text", $"Reply {i}")]);
        }
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store));
        await main.InitializeAsync();
        await WaitAsync(() => main.Conversations.Count == 1);
        main.SelectedConversation = main.Conversations[0];
        var conversation = main.CurrentConversation!;
        await WaitAsync(() => conversation.Turns.Count == 12);
        Assert.Equal(ConversationDisplayViewModel.VisibleTurns, main.Display.TurnFilterIndex);
        Assert.Equal("Turns (12 total)", conversation.ContextTurnsHeaderLabel);

        // Keep Recent 15 changes nothing; Keep Recent 10 hides the two oldest.
        await conversation.KeepRecentTurnsCommand.ExecuteAsync(null);
        Assert.StartsWith("Nothing to change", conversation.Status);
        conversation.SmartCleanupIndex = 0;
        await conversation.KeepRecentTurnsCommand.ExecuteAsync(null);
        var hydrated = await HydratedAsync(store, saved.Id);
        Assert.Equal([false, false, true, true, true, true, true, true, true, true, true, true], hydrated);
        Assert.Equal(10, conversation.ShownTurns.Count);
        Assert.Equal("12 stored turns · 10 in Claude's context", conversation.ContextStatusLabel);

        // A Shift-click selects the range from the last turn clicked; Hide leaves them out, and the list drops them.
        conversation.SelectContextTurn(conversation.ShownTurns[0], range: false);
        conversation.SelectContextTurn(conversation.ShownTurns[2], range: true);
        Assert.Equal("3 selected", conversation.SelectedTurnsLabel);
        Assert.Equal("Hide (3)", conversation.HideSelectedTurnsLabel);
        await conversation.HideSelectedTurnsCommand.ExecuteAsync(null);
        Assert.Equal("Hid turns 3, 4, 5 from Claude's context.", conversation.Status);
        Assert.Equal(5, (await HydratedAsync(store, saved.Id)).Count(h => !h));
        Assert.Equal(7, conversation.ShownTurns.Count);
        Assert.Equal("0 selected", conversation.SelectedTurnsLabel);
        Assert.False(conversation.DeleteSelectedTurnsCommand.CanExecute(null));

        // With all turns listed, Show puts the selected ones back; one already shown is left alone.
        main.Display.TurnFilterIndex = ConversationDisplayViewModel.AllTurns;
        conversation.SelectContextTurn(conversation.ShownTurns[0], range: false);
        conversation.SelectContextTurn(conversation.ShownTurns[5], range: false);
        await conversation.ShowSelectedTurnsCommand.ExecuteAsync(null);
        Assert.Equal("Put turn 1 back in Claude's context.", conversation.Status);
        Assert.True((await HydratedAsync(store, saved.Id))[0]);

        // Delete asks first, then removes the selected turns together.
        conversation.ClearContextTurnSelectionCommand.Execute(null);
        conversation.SelectAllContextTurnsCommand.Execute(null);
        Assert.Equal("12 selected", conversation.SelectedTurnsLabel);
        conversation.ClearContextTurnSelectionCommand.Execute(null);
        conversation.SelectContextTurn(conversation.ShownTurns[10], range: false);
        conversation.SelectContextTurn(conversation.ShownTurns[11], range: true);
        string? asked = null;
        conversation.ConfirmAsync = (_, message) => Task.FromResult((asked = message) is null);
        await conversation.DeleteSelectedTurnsCommand.ExecuteAsync(null);
        Assert.Equal(12, conversation.Turns.Count);
        conversation.ConfirmAsync = (_, _) => Task.FromResult(true);
        await conversation.DeleteSelectedTurnsCommand.ExecuteAsync(null);
        Assert.StartsWith("Delete turns 11, 12 and everything saved with them?", asked);
        Assert.Equal("Deleted turns 11, 12.", conversation.Status);
        Assert.Equal(10, conversation.Turns.Count);
        Assert.Equal(10, (await store.LoadTurnsAsync(saved.Id)).Count);

        // A selected turn the Show picker leaves out is deselected.
        conversation.SelectContextTurn(conversation.ShownTurns[0], range: false);
        main.Display.TurnFilterIndex = ConversationDisplayViewModel.HiddenTurns;
        Assert.Equal("0 selected", conversation.SelectedTurnsLabel);
    }

    [Fact]
    public async Task CopyMetricsCopiesTheChosenTurnOrTheWholeConversation()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var saved = await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        for (var i = 1; i <= 2; i++)
        {
            var turn = await store.StartTurnAsync(saved.Id, $"Prompt {i}");
            await store.FinishTurnAsync(turn.Id, TurnStatus.Completed, null, [new(2, "assistant", "text", $"Reply {i}")]);
        }
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store));
        await main.InitializeAsync();
        await WaitAsync(() => main.Conversations.Count == 1);
        main.SelectedConversation = main.Conversations[0];
        var conversation = main.CurrentConversation!;
        await WaitAsync(() => conversation.Turns.Count == 2);
        var (first, second) = (conversation.Turns[0], conversation.Turns[1]);
        string? copied = null;
        conversation.CopyTextAsync = text => Task.FromResult(copied = text);

        // Without a dialog host, the turn's metrics are copied.
        await conversation.CopyMetricsCommand.ExecuteAsync(second);
        Assert.Equal(second.FormatMetrics(), copied);

        // Canceling copies nothing.
        MetricsCopyScope? scope = null;
        conversation.ChooseMetricsCopyScopeAsync = () => Task.FromResult(scope);
        copied = null;
        await conversation.CopyMetricsCommand.ExecuteAsync(second);
        Assert.Null(copied);

        // The whole conversation separates each turn's metrics with a blank line.
        scope = MetricsCopyScope.Conversation;
        await conversation.CopyMetricsCommand.ExecuteAsync(second);
        Assert.Equal(first.FormatMetrics() + Environment.NewLine + Environment.NewLine + second.FormatMetrics(), copied);
        Assert.Equal("Copied the metrics for 2 turns.", conversation.Status);

        scope = MetricsCopyScope.Turn;
        await conversation.CopyMetricsCommand.ExecuteAsync(first);
        Assert.Equal(first.FormatMetrics(), copied);
        Assert.Equal("Copied the turn's metrics.", conversation.Status);
    }

    private static async Task<bool[]> HydratedAsync(ConversationStore store, long conversationId) =>
        (await store.LoadTurnsAsync(conversationId)).OrderBy(t => t.TurnNumber).Select(t => t.IsHydrated).ToArray();

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
