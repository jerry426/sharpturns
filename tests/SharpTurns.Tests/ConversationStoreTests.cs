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

        Assert.Equal(10L, Scalar("PRAGMA user_version"));
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
        var project = await _store.CreateProjectAsync("Beta", "/work/beta", "#9437ff");
        var alpha = await _store.CreateProjectAsync("alpha", "/work/alpha");
        await _store.UpdateProjectAsync(project with { WorkingDirectory = "/work/beta2" });
        var older = await _store.CreateConversationAsync(project.Id, "First");
        var newer = await _store.CreateConversationAsync(project.Id, "Second");
        await _store.RenameConversationAsync(older.Id, "Renamed");
        await _store.SetConversationModelAsync(older.Id, "sonnet", "high");
        await _store.SetConversationOutputStyleAsync(older.Id, "Concise");
        await _store.SetConversationAutoSummarizeAsync(older.Id, true);
        await _store.StartTurnAsync(older.Id, "hello");

        Assert.Equal(["alpha", "Beta"], (await _store.ListProjectsAsync()).Select(p => p.Name));
        Assert.Equal("/work/beta2", (await _store.ListProjectsAsync()).Single(p => p.Id == project.Id).WorkingDirectory);
        // Colors are stored as uppercase #RRGGBB; a project without one uses the default.
        Assert.Equal("#9437FF", (await _store.ListProjectsAsync()).Single(p => p.Id == project.Id).Color);
        Assert.Equal(ProjectColor.Default, alpha.Color);
        await _store.UpdateProjectAsync(alpha with { Color = "#12abef" });
        Assert.Equal("#12ABEF", (await _store.ListProjectsAsync()).Single(p => p.Id == alpha.Id).Color);
        var conversations = await _store.ListConversationsAsync(project.Id);
        // The conversation with the latest turn sorts first.
        Assert.Equal([older.Id, newer.Id], conversations.Select(c => c.Id));
        Assert.Equal(("Renamed", "sonnet", "high", "Concise", true), (conversations[0].Title, conversations[0].Model,
            conversations[0].Effort, conversations[0].OutputStyle, conversations[0].AutoSummarize));
        Assert.Equal((null, false), (conversations[1].OutputStyle, conversations[1].AutoSummarize));

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

        var image = new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=");
        var first = await _store.StartTurnAsync(conversation.Id, "first prompt", [image]);
        await _store.FinishTurnAsync(first.Id, TurnStatus.Completed, null,
            [new(3, "assistant", "thinking", "reasoning"), new(4, "assistant", "text", "answer")],
            new TurnUsage(1200, 1000, 80, 1150, 2, 1100, 900), "claude-haiku-4-5-20251001");
        var second = await _store.StartTurnAsync(conversation.Id, "second prompt");
        await _store.FinishTurnAsync(second.Id, TurnStatus.Failed, "CLI error", []);

        var turns = await _store.LoadTurnsAsync(conversation.Id);
        Assert.Equal([1, 2], turns.Select(t => t.TurnNumber));
        Assert.Equal(TurnStatus.Completed, turns[0].Status);
        Assert.NotNull(turns[0].FinishedAt);
        Assert.Equal(first.Parts, turns[0].Parts.Take(2));
        Assert.Equal(image, TurnParts.ReadImage(turns[0].Parts[1]));
        Assert.Equal(["reasoning", "answer"], turns[0].Parts.Skip(2).Select(p => p.Content));
        Assert.Equal(("assistant", "thinking"), (turns[0].Parts[2].Role, turns[0].Parts[2].PartType));
        Assert.Equal(new TurnUsage(1200, 1000, 80, 1150, 2, 1100, 900), turns[0].Usage);
        Assert.Equal("claude-haiku-4-5-20251001", turns[0].Model);
        Assert.Equal((TurnStatus.Failed, "CLI error"), (turns[1].Status, turns[1].ErrorMessage));
        Assert.Equal(["second prompt"], turns[1].Parts.Select(p => p.Content));
        Assert.Null(turns[1].Usage);
        Assert.Null(turns[1].Model);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.FinishTurnAsync(second.Id, TurnStatus.Running, null, []));
    }

    [Fact]
    public async Task ImageReplayChoicesAreSavedOnTheImagePart()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        var image = new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=");
        var turn = await _store.StartTurnAsync(conversation.Id, "look", [image, image with { FileName = "other.png" }]);

        // Saved before the choice existed, images read as not chosen.
        Assert.DoesNotContain("IncludeInFutureReplay", turn.Parts[1].Content);
        await _store.SetImageReplayAsync(turn.Id, 3, true);

        var parts = Assert.Single(await _store.LoadTurnsAsync(conversation.Id)).Parts;
        Assert.Equal(image, TurnParts.ReadImage(parts[1]));
        Assert.Equal(image with { FileName = "other.png", IncludeInFutureReplay = true }, TurnParts.ReadImage(parts[2]));
        Assert.Equal(TurnParts.WithImageReplay(turn.Parts[2], true), parts[2]);
        await _store.SetImageReplayAsync(turn.Id, 3, false);
        Assert.Equal(turn.Parts[2], (await _store.LoadTurnsAsync(conversation.Id))[0].Parts[2]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SetImageReplayAsync(turn.Id, 1, true));
    }

    [Fact]
    public async Task NotesBelongToTheirConversationAndGoWithIt()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        var other = await _store.CreateConversationAsync(project.Id, "Other");
        var first = await _store.CreateNoteAsync(conversation.Id, "First", "# Plan\n- step");
        var second = await _store.CreateNoteAsync(conversation.Id, "Second", "text");
        await _store.CreateNoteAsync(other.Id, "Elsewhere", "text");

        var updated = await _store.UpdateNoteAsync(first.Id, "First, edited", "# Plan\n- done");

        Assert.Equal((first.CreatedAt, "First, edited", "# Plan\n- done"), (updated.CreatedAt, updated.Title, updated.Content));
        Assert.True(updated.UpdatedAt >= first.UpdatedAt);
        // Listed in the order they were created, as in the Workbench, not by the latest edit.
        Assert.Equal([updated, second], await _store.ListNotesAsync(conversation.Id));
        Assert.Equal(2, await _store.CountNotesAsync(conversation.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateNoteAsync(conversation.Id, "Empty", " "));

        await _store.DeleteNoteAsync(second.Id);
        Assert.Equal(1, await _store.CountNotesAsync(conversation.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.UpdateNoteAsync(second.Id, "Gone", "text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteNoteAsync(second.Id));

        await _store.DeleteConversationAsync(conversation.Id);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM conversation_user_notes"));
    }

    [Fact]
    public async Task DeletingATurnRemovesItsPartsAndKeepsLaterNumbers()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        var first = await _store.StartTurnAsync(conversation.Id, "first", [new ImageAttachment("a.png", "image/png", "iVBORw0KGgo=")]);
        await _store.StartTurnAsync(conversation.Id, "second");

        await _store.DeleteTurnAsync(first.Id);

        var remaining = Assert.Single(await _store.LoadTurnsAsync(conversation.Id));
        Assert.Equal((2, "second"), (remaining.TurnNumber, remaining.Parts.Single().Content));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM conversation_turn_parts"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteTurnAsync(first.Id));
    }

    [Fact]
    public async Task UpgradeFromTheFirstSchemaKeepsExistingTurns()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation", "haiku");
        var withoutModel = await _store.CreateConversationAsync(project.Id, "Without model");
        await _store.SetSettingAsync("default_model", "opus");
        var turn = await _store.StartTurnAsync(conversation.Id, "prompt");
        await _store.FinishTurnAsync(turn.Id, TurnStatus.Completed, null, [new(2, "assistant", "text", "answer")]);
        foreach (var column in new[] { "input_tokens", "cached_input_tokens", "output_tokens", "context_tokens", "model",
                     "request_count", "first_request_input_tokens", "first_request_cached_tokens", "is_hydrated", "summary",
                     "summary_model" })
            Scalar($"ALTER TABLE conversation_turns DROP COLUMN {column}");
        Scalar("ALTER TABLE projects DROP COLUMN color");
        Scalar("ALTER TABLE conversations DROP COLUMN output_style");
        Scalar("ALTER TABLE conversations DROP COLUMN auto_summarize");
        Scalar("DROP TABLE conversation_user_notes");
        Scalar("DROP TABLE models");
        Scalar("DROP TABLE mcp_servers");
        Scalar("PRAGMA user_version = 1");
        // Pooled connections cache the schema this test just changed behind the store's back.
        SqliteConnection.ClearAllPools();

        await _store.InitializeAsync();

        Assert.Equal(10L, Scalar("PRAGMA user_version"));
        Assert.Equal(ProjectColor.Default, Assert.Single(await _store.ListProjectsAsync()).Color);
        var conversations = await _store.ListConversationsAsync(project.Id);
        var upgraded = Assert.Single(conversations, c => c.Id == conversation.Id);
        Assert.Equal((null, false), (upgraded.OutputStyle, upgraded.AutoSummarize));
        // Migration 9 lists the model IDs, moves the CLI aliases earlier builds saved to them, and gives a conversation
        // without a model the new conversation model.
        Assert.Equal(8, (await _store.ListModelsAsync()).Count);
        Assert.Equal("claude-haiku-4-5-20251001", upgraded.Model);
        Assert.Equal("claude-opus-5-5", await _store.GetSettingAsync("default_model"));
        Assert.Equal("claude-opus-5-5", Assert.Single(conversations, c => c.Id == withoutModel.Id).Model);
        Assert.Empty(await _store.ListMcpServersAsync());
        var loaded = Assert.Single(await _store.LoadTurnsAsync(conversation.Id));
        Assert.Equal(["prompt", "answer"], loaded.Parts.Select(p => p.Content));
        Assert.Null(loaded.Usage);
        Assert.Null(loaded.Model);
        Assert.Equal((true, null), (loaded.IsHydrated, loaded.Summary));
    }

    [Fact]
    public async Task TurnContextStateRoundTrips()
    {
        await _store.InitializeAsync();
        var project = await _store.CreateProjectAsync("Project", "/work");
        var conversation = await _store.CreateConversationAsync(project.Id, "Conversation");
        var turn = await _store.StartTurnAsync(conversation.Id, "prompt");

        await _store.SetTurnHydratedAsync(turn.Id, false);
        await _store.SetTurnSummaryAsync(turn.Id, "## Work Summary\n\n- Done.", "sonnet");
        var hidden = Assert.Single(await _store.LoadTurnsAsync(conversation.Id));
        Assert.Equal((false, "## Work Summary\n\n- Done.", "sonnet", true),
            (hidden.IsHydrated, hidden.Summary, hidden.SummaryModel, hidden.IsCompressed));

        await _store.SetTurnHydratedAsync(turn.Id, true);
        await _store.SetTurnSummaryAsync(turn.Id, null, "sonnet");
        var expanded = Assert.Single(await _store.LoadTurnsAsync(conversation.Id));
        Assert.Equal((true, null, null), (expanded.IsHydrated, expanded.Summary, expanded.SummaryModel));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SetTurnHydratedAsync(turn.Id + 1, false));
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
