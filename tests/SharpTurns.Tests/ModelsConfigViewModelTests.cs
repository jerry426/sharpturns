using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ModelsConfigViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-models-{Guid.NewGuid():N}");

    [Fact]
    public async Task RenamingAndDeletingAModelMovesWhatUsesIt()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-4-6");
        var preferences = new ApplicationPreferencesViewModel(store);
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store), preferences);
        await main.InitializeAsync();
        await WaitAsync(() => main.Conversations.Count == 1);
        main.SelectedConversation = main.Conversations[0];
        var conversation = main.CurrentConversation!;
        Assert.Equal("claude-opus-4-6", conversation.SelectedModel);

        var models = preferences.Models;
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-4-8", "claude-opus-4-7",
            "claude-sonnet-4-6", "claude-opus-4-6", "claude-haiku-4-5-20251001"], models.Models);
        Assert.Same(models.Models, conversation.ModelOptions);
        Assert.Equal("claude-sonnet-5-5", preferences.NewConversationModel);
        Assert.Equal(TurnSummarizer.DefaultModel, preferences.SummarizerModel);

        // Blank, spaced, and duplicate IDs are refused; a new one goes at the end.
        foreach (var (text, message) in new[] { (" ", "Enter a model ID"), ("my model", "A model ID can't contain spaces"),
                     ("Claude-Opus-5-5", "claude-opus-5-5 is already in the list.") })
        {
            models.ModelIdText = text;
            await models.AddModelCommand.ExecuteAsync(null);
            Assert.StartsWith(message, models.Message);
        }
        models.ModelIdText = " claude-test-1 ";
        await models.AddModelCommand.ExecuteAsync(null);
        Assert.Equal("claude-test-1", models.Models[^1]);
        Assert.Equal("claude-test-1", models.SelectedModel);

        // Renaming the summarizer's unsaved default saves the new ID for it.
        models.SelectedModel = "claude-sonnet-5-5";
        models.ModelIdText = "claude-sonnet-5-5-renamed";
        await models.RenameModelCommand.ExecuteAsync(null);
        Assert.Equal("claude-sonnet-5-5-renamed", models.Models[0]);
        Assert.Equal("claude-sonnet-5-5-renamed", await store.GetSettingAsync(TurnSummarizer.ModelSetting));
        await WaitAsync(() => preferences.SummarizerModel == "claude-sonnet-5-5-renamed");

        // Deleting an ID in use asks for its replacement among the others.
        preferences.NewConversationModel = "claude-opus-4-6";
        await WaitAsync(async () => await store.GetSettingAsync("default_model") == "claude-opus-4-6");
        string? asked = null;
        IReadOnlyList<string>? choices = null;
        models.ChooseReplacementAsync = (_, message, offered) =>
        {
            (asked, choices) = (message, offered);
            return Task.FromResult<string?>("claude-opus-5-5");
        };
        models.SelectedModel = "claude-opus-4-6";
        await models.DeleteModelCommand.ExecuteAsync(null);
        Assert.Equal("claude-opus-4-6 is used by 1 conversation and the new conversation model. Choose the model that replaces it there.",
            asked);
        Assert.DoesNotContain("claude-opus-4-6", choices!);
        Assert.DoesNotContain("claude-opus-4-6", models.Models);
        Assert.Equal("claude-opus-5-5", Assert.Single(await store.ListConversationsAsync(project.Id)).Model);
        Assert.Equal("claude-opus-5-5", await store.GetSettingAsync("default_model"));
        Assert.Equal("claude-opus-5-5", conversation.SelectedModel);
        Assert.Equal("claude-opus-5-5", conversation.Conversation.Model);
        await WaitAsync(() => preferences.NewConversationModel == "claude-opus-5-5");

        // An unused ID is deleted after confirming.
        var confirmed = false;
        models.ConfirmAsync = (_, _) => Task.FromResult(confirmed = true);
        models.SelectedModel = "claude-test-1";
        await models.DeleteModelCommand.ExecuteAsync(null);
        Assert.True(confirmed);
        Assert.DoesNotContain("claude-test-1", models.Models);

        // The order is saved, and the pickers' list follows it.
        models.SelectedModel = models.Models[0];
        Assert.False(models.MoveModelUpCommand.CanExecute(null));
        await models.MoveModelDownCommand.ExecuteAsync(null);
        Assert.Equal("claude-sonnet-5-5-renamed", models.Models[1]);
        Assert.True(models.MoveModelUpCommand.CanExecute(null));
        Assert.Equal(models.Models, await store.ListModelsAsync());
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++) await Task.Delay(20);
        Assert.True(await condition());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
