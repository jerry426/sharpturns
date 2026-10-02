using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ApiKeysConfigViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-apikeys-{Guid.NewGuid():N}");

    [Fact]
    public async Task TheDeepgramKeyIsSavedCanceledAndCleared()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store);
        await preferences.LoadAsync();
        var keys = preferences.ApiKeys;
        Assert.False(keys.IsConfigured);
        Assert.Equal("Missing", keys.StatusLabel);
        Assert.False(keys.SaveCommand.CanExecute(null));
        Assert.False(keys.ClearValueCommand.CanExecute(null));

        keys.ValueText = "  secret-key  ";
        keys.RevealValue = true;
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Equal("secret-key", await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));
        Assert.Equal("secret-key", keys.ValueText);
        Assert.True(keys.IsConfigured);
        Assert.False(keys.RevealValue);
        Assert.False(keys.HasChanges);
        Assert.DoesNotContain("secret-key", keys.Status);

        keys.ValueText = "other";
        Assert.True(keys.CancelCommand.CanExecute(null));
        keys.CancelCommand.Execute(null);
        Assert.Equal("secret-key", keys.ValueText);

        var reloaded = new ApplicationPreferencesViewModel(store);
        await reloaded.LoadAsync();
        Assert.Equal("secret-key", reloaded.ApiKeys.DeepgramApiKey);

        // As in the Workbench, saving a blank value clears the key, as Clear Value does.
        keys.ValueText = " ";
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Null(await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));
        Assert.False(keys.IsConfigured);

        keys.ValueText = "again";
        await keys.SaveCommand.ExecuteAsync(null);
        await keys.ClearValueCommand.ExecuteAsync(null);
        Assert.Null(await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));
        Assert.Equal("", keys.ValueText);
        Assert.Equal("Cleared the Deepgram API key.", keys.Status);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
