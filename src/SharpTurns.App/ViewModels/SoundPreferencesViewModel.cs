using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's Sounds, as in the Workbench: a sound when a turn finishes and one when a permission or question
/// dialog appears. Edits apply at once and are kept by Save Sounds; Reload brings back the saved choices.
/// </summary>
public sealed class SoundPreferencesViewModel : ObservableObject
{
    internal const string Setting = "sound_preferences";
    private readonly ConversationStore _store;
    private readonly IAppSoundPlayer _soundPlayer;
    private string _status = "Sound preferences use default notification sounds until loaded.";
    private bool _isLoading;
    private bool _isSaving;
    private bool _conversationTurnFinishedSoundEnabled = SoundNotificationPreference.ConversationTurnFinishedDefault.Enabled;
    private string _conversationTurnFinishedSound = SoundNotificationPreference.ConversationTurnFinishedDefault.SystemSound;
    private bool _commandApprovalSoundEnabled = SoundNotificationPreference.CommandApprovalDisplayedDefault.Enabled;
    private string _commandApprovalSound = SoundNotificationPreference.CommandApprovalDisplayedDefault.SystemSound;

    internal SoundPreferencesViewModel(ConversationStore store, IAppSoundPlayer soundPlayer)
    {
        _store = store;
        _soundPlayer = soundPlayer;
        ReloadCommand = new AsyncRelayCommand(LoadAsync, CanLoad);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        PreviewConversationTurnFinishedCommand = new RelayCommand(
            () => PlayNotification(AppSoundNotificationKind.ConversationTurnFinished));
        PreviewCommandApprovalCommand = new RelayCommand(
            () => PlayNotification(AppSoundNotificationKind.CommandApprovalDisplayed));
    }

    public IAsyncRelayCommand ReloadCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand PreviewConversationTurnFinishedCommand { get; }

    public IRelayCommand PreviewCommandApprovalCommand { get; }

    public IReadOnlyList<string> SystemSoundOptions => _soundPlayer.SystemSoundFileNames;

    public string ConversationTurnFinishedSoundDefaultLabel => $"Default: {SoundNotificationPreference.ConversationTurnFinishedDefault.SystemSound}";

    public string CommandApprovalSoundDefaultLabel => $"Default: {SoundNotificationPreference.CommandApprovalDisplayedDefault.SystemSound}";

    public string PlaybackDescription => _soundPlayer.PlaybackDescription;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyCommandStatesChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                NotifyCommandStatesChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool ConversationTurnFinishedSoundEnabled
    {
        get => _conversationTurnFinishedSoundEnabled;
        set => SetProperty(ref _conversationTurnFinishedSoundEnabled, value);
    }

    public string ConversationTurnFinishedSound
    {
        get => _conversationTurnFinishedSound;
        set => SetProperty(
            ref _conversationTurnFinishedSound,
            NormalizeSystemSoundName(value, SoundNotificationPreference.ConversationTurnFinishedDefault.SystemSound));
    }

    public bool CommandApprovalSoundEnabled
    {
        get => _commandApprovalSoundEnabled;
        set => SetProperty(ref _commandApprovalSoundEnabled, value);
    }

    public string CommandApprovalSound
    {
        get => _commandApprovalSound;
        set => SetProperty(
            ref _commandApprovalSound,
            NormalizeSystemSoundName(value, SoundNotificationPreference.CommandApprovalDisplayedDefault.SystemSound));
    }

    public SoundPreferences CurrentPreferences => new(
        NormalizeAvailableSystemSoundPreference(
            new SoundNotificationPreference(ConversationTurnFinishedSoundEnabled, ConversationTurnFinishedSound),
            SoundNotificationPreference.ConversationTurnFinishedDefault),
        NormalizeAvailableSystemSoundPreference(
            new SoundNotificationPreference(CommandApprovalSoundEnabled, CommandApprovalSound),
            SoundNotificationPreference.CommandApprovalDisplayedDefault));

    /// <summary>Owns its errors: preferences that can't be read keep the defaults, and the reason goes to the status.</summary>
    internal async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Apply(SoundPreferencesJson.Parse(await _store.GetSettingAsync(Setting)));
            Status = "Loaded sound preferences.";
        }
        catch (Exception ex)
        {
            Status = $"Unable to load sound preferences: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void PlayNotification(AppSoundNotificationKind kind)
    {
        var preference = CurrentPreferences.Get(kind);
        if (preference.Enabled)
        {
            _soundPlayer.PlaySystemSound(preference.SystemSound);
        }
    }

    private bool CanLoad() => !IsLoading && !IsSaving;

    private bool CanSave() => !IsLoading && !IsSaving;

    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var preferences = CurrentPreferences;
            await _store.SetSettingAsync(Setting, SoundPreferencesJson.ToJson(preferences));
            Apply(preferences);
            Status = "Saved sound preferences.";
        }
        catch (Exception ex)
        {
            Status = $"Unable to save sound preferences: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Apply(SoundPreferences preferences)
    {
        var normalized = new SoundPreferences(
            NormalizeAvailableSystemSoundPreference(
                preferences.ConversationTurnFinished,
                SoundNotificationPreference.ConversationTurnFinishedDefault),
            NormalizeAvailableSystemSoundPreference(
                preferences.CommandApprovalDisplayed,
                SoundNotificationPreference.CommandApprovalDisplayedDefault));

        ConversationTurnFinishedSoundEnabled = normalized.ConversationTurnFinished.Enabled;
        ConversationTurnFinishedSound = normalized.ConversationTurnFinished.SystemSound;
        CommandApprovalSoundEnabled = normalized.CommandApprovalDisplayed.Enabled;
        CommandApprovalSound = normalized.CommandApprovalDisplayed.SystemSound;
    }

    private SoundNotificationPreference NormalizeAvailableSystemSoundPreference(
        SoundNotificationPreference preference,
        SoundNotificationPreference fallback)
    {
        var normalized = preference.Normalize(fallback);
        if (SystemSoundOptions.Count == 0
            || SystemSoundOptions.Contains(normalized.SystemSound, StringComparer.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized with { SystemSound = fallback.SystemSound };
    }

    private string NormalizeSystemSoundName(string? value, string fallback)
    {
        var fileName = Path.GetFileName(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fallback;
        }

        return SystemSoundOptions.Count == 0
            || SystemSoundOptions.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                ? fileName
                : fallback;
    }

    private void NotifyCommandStatesChanged()
    {
        ReloadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
}
