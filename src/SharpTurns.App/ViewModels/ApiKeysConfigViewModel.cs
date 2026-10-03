using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's API Keys, with only the Deepgram key, which dictation uses. The key is
/// stored in plain text in the settings table and masked until revealed; it never goes into a status message or a log.
/// </summary>
public sealed partial class ApiKeysConfigViewModel : ObservableObject
{
    internal const string DeepgramSetting = "deepgram_api_key";
    private readonly ConversationStore _store;

    /// <summary>The key as edited; <see cref="DeepgramApiKey"/> holds the saved one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private string _valueText = "";

    [ObservableProperty]
    private bool _revealValue;

    [ObservableProperty]
    private string _status = "API keys not loaded.";

    internal ApiKeysConfigViewModel(ConversationStore store)
    {
        _store = store;
    }

    /// <summary>The saved Deepgram key; null when none is saved.</summary>
    public string? DeepgramApiKey { get; private set; }

    public bool IsConfigured => DeepgramApiKey is not null;

    public string StatusLabel => IsConfigured ? "Configured" : "Missing";

    public bool HasChanges => Normalize(ValueText) != DeepgramApiKey;

    /// <summary>Owns its errors: a key that can't be read stays missing, and the reason goes to the status.</summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        try
        {
            Apply(Normalize(await _store.GetSettingAsync(DeepgramSetting)));
            Status = "Loaded API keys. The key is masked until you check Reveal value.";
        }
        catch (Exception e) { Status = "Couldn't load the API keys: " + e.Message; }
    }

    private bool CanSave() => HasChanges;

    /// <summary>Saving a blank value clears the stored key.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => StoreAsync(Normalize(ValueText));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Cancel()
    {
        Apply(DeepgramApiKey);
        Status = "Discarded unsaved API key changes.";
    }

    private bool CanClearValue() => IsConfigured;

    [RelayCommand(CanExecute = nameof(CanClearValue))]
    private Task ClearValueAsync() => StoreAsync(null);

    // Null removes the setting.
    private async Task StoreAsync(string? key)
    {
        try
        {
            await _store.SetSettingAsync(DeepgramSetting, key);
            Apply(key);
            Status = key is null ? "Cleared the Deepgram API key." : "Saved the Deepgram API key.";
        }
        catch (Exception e) { Status = "Couldn't save the Deepgram API key: " + e.Message; }
    }

    private void Apply(string? key)
    {
        DeepgramApiKey = key;
        ValueText = key ?? "";
        RevealValue = false;
        OnPropertyChanged(nameof(DeepgramApiKey));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(StatusLabel));
        // ValueText may not have changed, so its notifications may not have run.
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearValueCommand.NotifyCanExecuteChanged();
    }

    private static string? Normalize(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
