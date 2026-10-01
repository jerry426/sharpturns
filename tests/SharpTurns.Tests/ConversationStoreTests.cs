using Microsoft.Data.Sqlite;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-store-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly ConversationStore _store;

    public ConversationStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "test.db");
        _store = new ConversationStore(_path);
    }

    [Fact]
    public async Task InitializeAppliesMigrationsInWalModeAndCanRunAgain()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        Assert.Equal(1L, Scalar("PRAGMA user_version"));
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
    }

    [Fact]
    public async Task InitializeRejectsANewerSchema()
    {
        await _store.InitializeAsync();
        Scalar("PRAGMA user_version = 99");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.InitializeAsync());
    }

    [Fact]
    public async Task ProjectsAndConversationsRoundTripAndDeleteCascades()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Beta", "/work/beta");
        await _store.CreateProjectAsync("alpha", "/work/alpha");
        await _store.UpdateProjectAsync(project with { WorkingDirectory = "/work/beta2" });
        var older = await _store.CreateConversationAsync(project.Id, "First");
        var newer = await _store.CreateConversationAsync(project.Id, "Second");
        await _store.RenameConversationAsync(older.Id, "Renamed");
        await _store.SetConversationModelAsync(older.Id, "sonnet", "high");
        await _store.StartTurnAsync(older.Id, "hello");

        Assert.Equal(["alpha", "Beta"], (await _store.ListProjectsAsync()).Select(p => p.Name));
        Assert.Equal("/work/beta2", (await _store.ListProjectsAsync()).Single(p => p.Id == project.Id).WorkingDirectory);
        var conversations = await _store.ListConversationsAsync(project.Id);
        // The conversation with the latest turn sorts first.
        Assert.Equal([older.Id, newer.Id], conversations.Select(c => c.Id));
        Assert.Equal(("Renamed", "sonnet", "high"), (conversations[0].Title, conversations[0].Model, conversations[0].Effort));

        await _store.DeleteProjectAsync(project.Id);

        Assert.Empty(await _store.ListConversationsAsync(project.Id));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM conversation_turn_parts"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.RenameConversationAsync(older.Id, "Gone"));
    }

    [Fact]
    public async Task TurnsKeepTheirPromptStatusAndResponseParts()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");

        var first = await _store.StartTurnAsync(conversation.Id, "first prompt");
        await _store.FinishTurnAsync(first.Id, TurnStatus.Completed, null,
            [new(2, "assistant", "thinking", "reasoning"), new(3, "assistant", "text", "answer")]);
        var second = await _store.StartTurnAsync(conversation.Id, "second prompt");
        await _store.FinishTurnAsync(second.Id, TurnStatus.Failed, "CLI error", []);

        var turns = await _store.LoadTurnsAsync(conversation.Id);
        Assert.Equal([1, 2], turns.Select(t => t.TurnNumber));
        Assert.Equal(TurnStatus.Completed, turns[0].Status);
        Assert.NotNull(turns[0].FinishedAt);
        Assert.Equal(["first prompt", "reasoning", "answer"], turns[0].Parts.Select(p => p.Content));
        Assert.Equal(("assistant", "thinking"), (turns[0].Parts[1].Role, turns[0].Parts[1].PartType));
        Assert.Equal((TurnStatus.Failed, "CLI error"), (turns[1].Status, turns[1].ErrorMessage));
        Assert.Equal(["second prompt"], turns[1].Parts.Select(p => p.Content));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.FinishTurnAsync(second.Id, TurnStatus.Running, null, []));
    }

    [Fact]
    public async Task InitializeMarksTurnsLeftRunningAsFailed()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        await _store.StartTurnAsync(conversation.Id, "prompt");

        await _store.InitializeAsync();

        var turn = Assert.Single(await _store.LoadTurnsAsync(conversation.Id));
        Assert.Equal(TurnStatus.Failed, turn.Status);
        Assert.Equal("The app closed before this turn finished.", turn.ErrorMessage);
    }

    [Fact]
    public async Task SessionStateAndSettingsRoundTrip()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        var state = new ClaudeCodeSessionState("700c7fa5-e552-450e-8712-3ebb74e2857c", "/work", "F", InFlight: true, "policy");

        Assert.Null(await _store.LoadClaudeCodeSessionAsync(conversation.Id));
        await _store.SaveClaudeCodeSessionAsync(conversation.Id, state);
        Assert.Equal(state, await _store.LoadClaudeCodeSessionAsync(conversation.Id));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveClaudeCodeSessionAsync(conversation.Id, state with { SessionId = "not-a-uuid" }));

        await _store.SetSettingAsync("key", "one");
        await _store.SetSettingAsync("key", "two");
        Assert.Equal("two", await _store.GetSettingAsync("key"));
        await _store.SetSettingAsync("key", null);
        Assert.Null(await _store.GetSettingAsync("key"));
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
