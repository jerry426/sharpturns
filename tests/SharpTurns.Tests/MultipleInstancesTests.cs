using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

// Each MainWindowViewModel stands in for one SharpTurns instance; their ConversationLocks share a folder, as instances
// sharing a database do.
public sealed class MultipleInstancesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-instances-{Guid.NewGuid():N}");
    private readonly List<ConversationLocks> _locks = [];

    [Fact]
    public void ALockKeepsOtherInstancesOutAndTellsWhetherOneHeldItSince()
    {
        var folder = Path.Combine(_directory, "locks");
        var first = Locks(folder);
        var second = Locks(folder);

        Assert.True(first.TryAcquire(1, out var heldElsewhere));
        Assert.True(heldElsewhere); // Never held here, so what's loaded can't be trusted.
        Assert.False(second.TryAcquire(1));
        Assert.True(second.TryAcquire(2));

        first.Release(1);
        Assert.True(first.TryAcquire(1, out heldElsewhere));
        Assert.False(heldElsewhere);
        first.Release(1);
        Assert.True(second.TryAcquire(1));
        second.Release(1);
        Assert.True(first.TryAcquire(1, out heldElsewhere));
        Assert.True(heldElsewhere);

        first.Dispose();
        Assert.True(second.TryAcquire(1));
    }

    [Fact]
    public async Task InstancesHandAConversationOverWhenTheHolderSwitchesAway()
    {
        var (store, project, older, newer) = await CreateAsync();
        var a = await StartAsync(store);
        Assert.Equal(newer.Id, a.CurrentConversation?.Conversation.Id);

        // The second instance restores the same conversation and waits for it.
        var b = await StartAsync(store);
        Assert.Null(b.CurrentConversation);
        Assert.Equal(newer.Id, b.LockedConversation?.Id);
        Assert.Equal("\"Newer\" is open in another SharpTurns instance. It opens here once that instance switches to another conversation or closes.",
            b.NoConversationText);
        Assert.Null(b.CaptureInstanceSnapshot().ConversationId);
        // Neither delete reaches a conversation another instance holds.
        b.ConfirmAsync = (_, _) => Task.FromResult(true);
        await b.DeleteConversationCommand.ExecuteAsync(null);
        Assert.Equal("\"Newer\" is open in another SharpTurns instance, so it can't be deleted here.", b.ErrorMessage);
        await b.DeleteProjectCommand.ExecuteAsync(null);
        Assert.Equal("\"Newer\" is open in another SharpTurns instance, so this project can't be deleted.", b.ErrorMessage);
        Assert.Equal(2, (await store.ListConversationsAsync(project.Id)).Count);

        b.SelectedConversation = b.Conversations.Single(c => c.Id == older.Id);
        Assert.Null(b.LockedConversation);
        Assert.Equal(older.Id, b.CurrentConversation?.Conversation.Id);

        // Switching away from an idle conversation lets it go, and the draft stays.
        var shown = a.CurrentConversation!;
        shown.ComposerText = "draft";
        a.SelectedConversation = a.Conversations.Single(c => c.Id == older.Id);
        Assert.Equal(older.Id, a.LockedConversation?.Id);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == newer.Id);
        Assert.Equal(newer.Id, b.CurrentConversation?.Conversation.Id);

        // Changed while the second instance held it, so the first reloads it on taking it back.
        await store.SetConversationModelAsync(newer.Id, "claude-haiku-4-5-20251001", "low");
        var turn = await store.StartTurnAsync(newer.Id, "From the other instance");
        await store.FinishTurnAsync(turn.Id, TurnStatus.Completed, null, [new(2, "assistant", "text", "Reply")]);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == older.Id);
        a.SelectedConversation = a.Conversations.Single(c => c.Id == newer.Id);
        Assert.Same(shown, a.CurrentConversation);
        await WaitAsync(() => shown.Turns.Count == 1);
        Assert.Equal(("claude-haiku-4-5-20251001", "low", "draft"), (shown.SelectedModel, shown.SelectedEffort, shown.ComposerText));
        Assert.Equal(new InstanceSnapshot(project.Id, "Project", project.Color, newer.Id, "Newer", "claude-haiku-4-5-20251001", false),
            a.CaptureInstanceSnapshot());

        // Taken back with no other instance holding it in between, nothing is reloaded.
        var third = await store.CreateConversationAsync(project.Id, "Third");
        a.Conversations.Insert(0, third);
        a.SelectedConversation = third;
        await store.StartTurnAsync(newer.Id, "Not reloaded");
        a.SelectedConversation = a.Conversations.Single(c => c.Id == newer.Id);
        await Task.Delay(200);
        Assert.Single(shown.Turns);
    }

    [Fact]
    public async Task AConversationRunningATurnKeepsItsLockUntilTheTurnEnds()
    {
        var (store, _, older, newer) = await CreateAsync();
        var a = await StartAsync(store);
        var running = a.CurrentConversation!;
        running.IsRunning = true;
        Assert.True(a.IsAnyTurnActive);
        Assert.True(a.CaptureInstanceSnapshot().TurnActive);
        a.SelectedConversation = a.Conversations.Single(c => c.Id == older.Id);

        var b = await StartAsync(store);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == newer.Id);
        Assert.Equal(newer.Id, b.LockedConversation?.Id);

        // A summary keeps it too, and it's released once nothing runs in it.
        running.IsCompressing = true;
        running.IsRunning = false;
        b.SelectedConversation = b.Conversations.Single(c => c.Id == older.Id);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == newer.Id);
        Assert.Equal(newer.Id, b.LockedConversation?.Id);
        running.IsCompressing = false;
        Assert.False(a.IsAnyTurnActive);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == older.Id);
        b.SelectedConversation = b.Conversations.Single(c => c.Id == newer.Id);
        Assert.Null(b.LockedConversation);
        Assert.Equal(newer.Id, b.CurrentConversation?.Conversation.Id);
    }

    [Fact]
    public async Task StartingUpFailsOnlyTurnsNoLiveInstanceIsRunning()
    {
        var (store, _, older, newer) = await CreateAsync();
        var a = await StartAsync(store);
        a.SelectedConversation = a.Conversations.Single(c => c.Id == older.Id);
        await store.StartTurnAsync(older.Id, "Still running in the first instance");
        await store.StartTurnAsync(newer.Id, "Left by an instance that exited");

        await StartAsync(store);

        Assert.Equal(TurnStatus.Running, Assert.Single(await store.LoadTurnsAsync(older.Id)).Status);
        Assert.Equal(TurnStatus.Failed, Assert.Single(await store.LoadTurnsAsync(newer.Id)).Status);
    }

    [Fact]
    public async Task AStartupSessionFromReloadOpensItsConversation()
    {
        var (store, project, older, _) = await CreateAsync();
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store), locks: Locks(Path.Combine(_directory, "locks")));

        await main.InitializeAsync(new AppStartupSession(project.Id, older.Id));

        await WaitAsync(() => main.CurrentConversation is not null);
        Assert.Equal(older.Id, main.CurrentConversation!.Conversation.Id);
    }

    private ConversationLocks Locks(string folder)
    {
        var locks = new ConversationLocks(folder);
        _locks.Add(locks);
        return locks;
    }

    private async Task<MainWindowViewModel> StartAsync(ConversationStore store)
    {
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store), locks: Locks(Path.Combine(_directory, "locks")));
        await main.InitializeAsync();
        await WaitAsync(() => main.CurrentConversation is not null || main.LockedConversation is not null);
        return main;
    }

    private async Task<(ConversationStore Store, Project Project, Conversation Older, Conversation Newer)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var older = await store.CreateConversationAsync(project.Id, "Older", "claude-opus-5-5");
        await Task.Delay(20); // The list sorts by updated time.
        var newer = await store.CreateConversationAsync(project.Id, "Newer", "claude-opus-5-5");
        return (store, project, older, newer);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    public void Dispose()
    {
        foreach (var locks in _locks) locks.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
