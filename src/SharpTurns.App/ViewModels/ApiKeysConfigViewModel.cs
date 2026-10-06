using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's API Keys, with only the Deepgram key, which dictation uses. The key is saved in the OS's credential
/// store or, if the user picks it, in plain text in the settings table; with no key saved, dictation uses
/// DEEPGRAM_API_KEY. The key is masked until revealed; it never goes into a status message or a log.
/// </summary>
public sealed partial class ApiKeysConfigViewModel : ObservableObject
{
    internal const string DeepgramSetting = "deepgram_api_key";
    internal const string DeepgramEnvironmentVariable = "DEEPGRAM_API_KEY";
    private readonly ConversationStore _store;
    private readonly ISecretStore? _secrets;
    private readonly string? _environmentKey;

    /// <summary>The key as edited; <see cref="DeepgramApiKey"/> holds the saved one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private string _valueText = "";

    /// <summary>Where Save puts the key, as chosen; <see cref="IsSavedInDatabase"/> holds where it's saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCredentialStore), nameof(UseDatabase), nameof(HasChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _storeInDatabase;

    [ObservableProperty]
    private bool _revealValue;

    [ObservableProperty]
    private string _status = "API keys not loaded.";

    /// <param name="secrets">The OS's credential store; null where there's none, so only the settings table is offered.</param>
    /// <param name="environmentKey">DEEPGRAM_API_KEY at launch.</param>
    internal ApiKeysConfigViewModel(ConversationStore store, ISecretStore? secrets, string? environmentKey)
    {
        _store = store;
        _secrets = secrets;
        _environmentKey = Normalize(environmentKey);
        _storeInDatabase = secrets is null;
    }

    /// <summary>The saved Deepgram key; null when none is saved.</summary>
    public string? DeepgramApiKey { get; private set; }

    public bool IsSavedInDatabase { get; private set; }

    /// <summary>The key dictation uses: the saved one, or else DEEPGRAM_API_KEY.</summary>
    public string? DictationApiKey => DeepgramApiKey ?? _environmentKey;

    public bool IsConfigured => DictationApiKey is not null;

    public string StatusLabel => DeepgramApiKey is not null ? (IsSavedInDatabase ? "Saved in database" : "Saved in credential store")
        : _environmentKey is not null ? "Using " + DeepgramEnvironmentVariable
        : "Missing";

    public bool HasCredentialStore => _secrets is not null;

    public string CredentialStoreLabel => _secrets?.Description ?? "OS credential store (none found)";

    public bool UseCredentialStore
    {
        get => !StoreInDatabase;
        set { if (value) StoreInDatabase = false; }
    }

    public bool UseDatabase
    {
        get => StoreInDatabase;
        set { if (value) StoreInDatabase = true; }
    }

    public bool HasChanges => Normalize(ValueText) is var key
        && (key != DeepgramApiKey || (key is not null && StoreInDatabase != IsSavedInDatabase));

    /// <summary>
    /// Owns its errors: a key that can't be read stays missing, and the reason goes to the status. A key in the
    /// credential store wins over one in the settings table.
    /// </summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        try
        {
            var key = _secrets is null ? null : Normalize(await _secrets.ReadAsync(DeepgramSetting));
            var inDatabase = false;
            if (key is null)
            {
                key = Normalize(await _store.GetSettingAsync(DeepgramSetting));
                inDatabase = key is not null;
            }
            Apply(key, inDatabase);
            Status = "Loaded API keys. The key is masked until you check Reveal value.";
        }
        catch (Exception e) { Status = "Couldn't load the API keys: " + e.Message; }
    }

    private bool CanSave() => HasChanges;

    /// <summary>Saving a blank value clears the stored key.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => StoreAsync(Normalize(ValueText), StoreInDatabase);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Cancel()
    {
        Apply(DeepgramApiKey, IsSavedInDatabase);
        Status = "Discarded unsaved API key changes.";
    }

    private bool CanClearValue() => DeepgramApiKey is not null;

    [RelayCommand(CanExecute = nameof(CanClearValue))]
    private Task ClearValueAsync() => StoreAsync(null, false);

    // Null removes the key. The key is saved before any old copy is removed, so a failure can't lose it; a key leaving
    // the settings table is erased from the file.
    private async Task StoreAsync(string? key, bool inDatabase)
    {
        try
        {
            if (key is not null && inDatabase) await _store.SetSettingAsync(DeepgramSetting, key);
            else if (key is not null) await _secrets!.WriteAsync(DeepgramSetting, key);
            if (key is null || !inDatabase) await _store.EraseSettingAsync(DeepgramSetting);
            if ((key is null || inDatabase) && _secrets is not null) await _secrets.WriteAsync(DeepgramSetting, null);
            Apply(key, inDatabase);
            Status = key is null ? "Cleared the Deepgram API key."
                : inDatabase ? "Saved the Deepgram API key in SharpTurns' database."
                : $"Saved the Deepgram API key in the {_secrets!.Description}.";
        }
        catch (Exception e) { Status = "Couldn't save the Deepgram API key: " + e.Message; }
    }

    private void Apply(string? key, bool inDatabase)
    {
        DeepgramApiKey = key;
        IsSavedInDatabase = key is not null && inDatabase;
        ValueText = key ?? "";
        // With no key saved, the choice goes back to the credential store where there is one.
        StoreInDatabase = key is null ? _secrets is null : inDatabase;
        RevealValue = false;
        OnPropertyChanged(nameof(DeepgramApiKey));
        OnPropertyChanged(nameof(IsSavedInDatabase));
        OnPropertyChanged(nameof(DictationApiKey));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(StatusLabel));
        // ValueText and StoreInDatabase may not have changed, so their notifications may not have run.
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearValueCommand.NotifyCanExecuteChanged();
    }

    private static string? Normalize(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
