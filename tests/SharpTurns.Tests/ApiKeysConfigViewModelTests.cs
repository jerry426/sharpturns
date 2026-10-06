using SharpTurns.App.Services;
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

        // Saving a blank value clears the key, as Clear Value does.
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

    [Fact]
    public async Task TheKeyGoesInTheCredentialStoreAndMovesToTheDatabaseOnlyWhenChosen()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var secrets = new FakeSecretStore();
        var preferences = new ApplicationPreferencesViewModel(store, secrets: secrets);
        await preferences.LoadAsync();
        var keys = preferences.ApiKeys;
        Assert.True(keys.UseCredentialStore);

        keys.ValueText = "secret-key";
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Equal("secret-key", secrets.Values[ApiKeysConfigViewModel.DeepgramSetting]);
        Assert.Null(await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));
        Assert.Equal("Saved in credential store", keys.StatusLabel);
        Assert.DoesNotContain("secret-key", keys.Status);

        // Choosing the other place is a change to save, and saving moves the key.
        keys.UseDatabase = true;
        Assert.True(keys.HasChanges);
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Empty(secrets.Values);
        Assert.Equal("secret-key", await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));
        Assert.True(keys.IsSavedInDatabase);

        var reloaded = new ApplicationPreferencesViewModel(store, secrets: secrets);
        await reloaded.LoadAsync();
        Assert.Equal("secret-key", reloaded.ApiKeys.DeepgramApiKey);
        Assert.True(reloaded.ApiKeys.UseDatabase);

        keys.UseCredentialStore = true;
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Equal("secret-key", secrets.Values[ApiKeysConfigViewModel.DeepgramSetting]);
        Assert.Null(await store.GetSettingAsync(ApiKeysConfigViewModel.DeepgramSetting));

        await keys.ClearValueCommand.ExecuteAsync(null);
        Assert.Empty(secrets.Values);
        Assert.False(keys.IsConfigured);
    }

    [Fact]
    public async Task DictationUsesTheEnvironmentKeyOnlyWhenNoKeyIsSaved()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store, secrets: new FakeSecretStore(), deepgramEnvironmentKey: " env-key ");
        await preferences.LoadAsync();
        var keys = preferences.ApiKeys;
        Assert.Null(keys.DeepgramApiKey);
        Assert.Equal("env-key", keys.DictationApiKey);
        Assert.True(keys.IsConfigured);
        Assert.Equal("Using DEEPGRAM_API_KEY", keys.StatusLabel);
        Assert.False(keys.ClearValueCommand.CanExecute(null));

        keys.ValueText = "saved-key";
        await keys.SaveCommand.ExecuteAsync(null);
        Assert.Equal("saved-key", keys.DictationApiKey);

        await keys.ClearValueCommand.ExecuteAsync(null);
        Assert.Equal("env-key", keys.DictationApiKey);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string Description => "test credential store";

        public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(name));

        public Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default)
        {
            if (value is null) Values.Remove(name);
            else Values[name] = value;
            return Task.CompletedTask;
        }
    }
}
