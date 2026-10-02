using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.ClaudeCli;
using SharpTurns.Core.Persistence;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's Preferences. As in the Workbench: restoring the last session at startup, the startup, Notes, and
/// Markdown Viewer window sizes, and the DOCX export defaults. SharpTurns adds the claude path, the model and effort for
/// new conversations, the summarizer model, and the system prompt. Each is saved in the settings table when it changes.
/// The Models subtab's list, which the model pickers offer, and the Sounds and API Keys subtabs live here too.
/// </summary>
public sealed partial class ApplicationPreferencesViewModel : ObservableObject
{
    private const string RestoreLastSessionSetting = "restore_last_session";
    private const string DocxExportSetting = "docx_export_settings";
    private const string ClaudePathSetting = "claude_path";
    internal const string NewConversationModelSetting = "default_model";
    private const string NewConversationEffortSetting = "default_effort";
    private const string SystemPromptSetting = "system_prompt";
    private readonly ConversationStore _store;
    private bool _isLoading;

    /// <summary>The claude path as typed; <see cref="ClaudePath"/> holds the saved one.</summary>
    [ObservableProperty]
    private string _claudePathText = "";

    /// <summary>The saved model, or the first listed one; null only before the list loads.</summary>
    [ObservableProperty]
    private string? _newConversationModel;

    [ObservableProperty]
    private string _newConversationEffort = ConversationViewModel.DefaultEffort;

    [ObservableProperty]
    private string _summarizerModel = TurnSummarizer.DefaultModel;

    /// <summary>The outcome of the last change in the Claude CLI section.</summary>
    [ObservableProperty]
    private string _claudeCliMessage = UsingClaudePathLabel(null);

    /// <summary>The system prompt as edited; <see cref="SystemPrompt"/> holds the saved one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSystemPromptChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveSystemPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSystemPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSystemPromptCommand))]
    private string _systemPromptText = ClaudeCliCodingPolicy.DefaultSystemPrompt;

    [ObservableProperty]
    private string _systemPromptMessage = SystemPromptLabel(ClaudeCliCodingPolicy.DefaultSystemPrompt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreLastSessionDescription))]
    private bool _restoreLastSession = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocxExportSettingsSummary))]
    private DocxExportSettings _docxExportSettings = DocxExportSettings.Default;

    [ObservableProperty]
    private string _status = "";

    /// <param name="soundPlayer">Null uses the platform's system sounds.</param>
    internal ApplicationPreferencesViewModel(ConversationStore store, IAppSoundPlayer? soundPlayer = null)
    {
        _store = store;
        Models = new(store);
        Sounds = new(store, soundPlayer ?? AppSoundPlayerFactory.CreateDefault());
        ApiKeys = new(store);
        Models.ModelsChanged += (_, _) => _ = ReloadModelSettingsAsync();
        StartupWindow = new(store, "startup_window_size", "startup window size", new(1400, 860), new(820, 560));
        NotesWindow = new(store, "notes_window_size", "Notes window size", new(980, 660), new(820, 520));
        MarkdownViewerWindow = new(store, "markdown_viewer_window_size", "Markdown Viewer window size",
            new(MarkdownViewerWindowDefaults.DefaultWidth, MarkdownViewerWindowDefaults.DefaultHeight),
            new(MarkdownViewerWindowDefaults.MinWidth, MarkdownViewerWindowDefaults.MinHeight));
    }

    /// <summary>The Config tab's Models subtab.</summary>
    public ModelsConfigViewModel Models { get; }

    /// <summary>The Config tab's Sounds subtab.</summary>
    public SoundPreferencesViewModel Sounds { get; }

    /// <summary>The Config tab's API Keys subtab.</summary>
    public ApiKeysConfigViewModel ApiKeys { get; }

    public WindowSizePreferenceViewModel StartupWindow { get; }

    public WindowSizePreferenceViewModel NotesWindow { get; }

    public WindowSizePreferenceViewModel MarkdownViewerWindow { get; }

    public string RestoreLastSessionDescription => RestoreLastSession
        ? "SharpTurns will restore your last project and conversation on restart."
        : "SharpTurns will start with the first project and its most recent conversation.";

    public string DocxExportSettingsSummary => DocxExportSettings.SummaryLabel;

    /// <summary>The saved claude executable; null runs claude from the PATH.</summary>
    public string? ClaudePath { get; private set; }

    /// <summary>Every model picker's list; see <see cref="ModelsConfigViewModel.Models"/>.</summary>
    public IReadOnlyList<string> ModelOptions => Models.Models;

    public IReadOnlyList<string> EffortOptions => ConversationViewModel.SharedEffortOptions;

    public string? NewConversationEffortOrNull => NewConversationEffort == ConversationViewModel.DefaultEffort ? null : NewConversationEffort;

    /// <summary>The saved system prompt, appended to the CLI's own for every turn.</summary>
    public string SystemPrompt { get; private set; } = ClaudeCliCodingPolicy.DefaultSystemPrompt;

    public bool HasSystemPromptChanges => SystemPromptText != SystemPrompt;

    /// <summary>Shows the DOCX export defaults dialog; returns the new settings, or null when canceled.</summary>
    public Func<DocxExportDefaultsDialogViewModel, Task<DocxExportSettings?>>? ShowDocxExportDefaultsDialogAsync { get; set; }

    /// <summary>Owns its errors: a preference that can't be read keeps its default, and the reason goes to the status.</summary>
    internal async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            await Models.LoadAsync();
            await Sounds.LoadAsync();
            await ApiKeys.LoadAsync();
            RestoreLastSession = await _store.GetSettingAsync(RestoreLastSessionSetting) != "false";
            DocxExportSettings = DocxExportSettings.Parse(await _store.GetSettingAsync(DocxExportSetting));
            await StartupWindow.LoadAsync();
            await NotesWindow.LoadAsync();
            await MarkdownViewerWindow.LoadAsync();
            ApplyClaudePath(await _store.GetSettingAsync(ClaudePathSetting) is { Length: > 0 } path ? path : null);
            ClaudeCliMessage = UsingClaudePathLabel(ClaudePath);
            NewConversationModel = Option(ModelOptions, await _store.GetSettingAsync(NewConversationModelSetting)) ?? ModelOptions.FirstOrDefault();
            NewConversationEffort = Option(EffortOptions, await _store.GetSettingAsync(NewConversationEffortSetting)) ?? ConversationViewModel.DefaultEffort;
            SummarizerModel = Option(ModelOptions, await _store.GetSettingAsync(TurnSummarizer.ModelSetting)) ?? TurnSummarizer.DefaultModel;
            ApplySystemPrompt(await _store.GetSettingAsync(SystemPromptSetting) is { Length: > 0 } prompt
                ? prompt : ClaudeCliCodingPolicy.DefaultSystemPrompt);
            SystemPromptMessage = SystemPromptLabel(SystemPrompt);
        }
        catch (Exception e) { Status = "Couldn't load the preferences: " + e.Message; }
        finally { _isLoading = false; }
    }

    // Null for a value not saved or no longer offered, so the caller's fallback applies.
    private static string? Option(IReadOnlyList<string> options, string? value) =>
        value is not null && options.Contains(value) ? value : null;

    // Owns its errors so the list's event can fire and forget it. The store has already moved a replaced ID's settings;
    // reading them back also restores a picker selection the list change cleared.
    private async Task ReloadModelSettingsAsync()
    {
        try
        {
            var newConversationModel = await _store.GetSettingAsync(NewConversationModelSetting);
            var summarizerModel = await _store.GetSettingAsync(TurnSummarizer.ModelSetting);
            var before = (NewConversationModel, SummarizerModel);
            _isLoading = true;
            try
            {
                NewConversationModel = Option(ModelOptions, newConversationModel) ?? ModelOptions.FirstOrDefault();
                SummarizerModel = Option(ModelOptions, summarizerModel) ?? TurnSummarizer.DefaultModel;
            }
            finally { _isLoading = false; }
            OnPropertyChanged(nameof(NewConversationModel));
            OnPropertyChanged(nameof(SummarizerModel));
            // A renamed or deleted ID moved them, so the last save's message would name the old one.
            if (before != (NewConversationModel, SummarizerModel))
                ClaudeCliMessage = $"The Models list changed: new conversations will use {NewConversationModel}, "
                    + $"and turns will be compressed with {SummarizerModel}.";
        }
        catch (Exception e) { ClaudeCliMessage = "Couldn't reload the model settings: " + e.Message; }
    }

    [RelayCommand]
    private async Task SaveClaudePathAsync()
    {
        var path = ClaudePathText.Trim();
        if (path.Length == 0)
        {
            await StoreClaudePathAsync(null);
            return;
        }
        if (!Path.IsPathFullyQualified(path))
            ClaudeCliMessage = "Enter the full path to the claude executable, or leave it blank to use claude from your PATH.";
        else if (OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ClaudeCliMessage = "Choose the native claude.exe, not a shell or .cmd wrapper.";
        else if (!File.Exists(path))
            ClaudeCliMessage = $"There's no file at {path}.";
        else
            await StoreClaudePathAsync(path);
    }

    [RelayCommand]
    private Task ResetClaudePathAsync() => StoreClaudePathAsync(null);

    // Null removes the setting, so turns run claude from the PATH.
    private async Task StoreClaudePathAsync(string? path)
    {
        try
        {
            await _store.SetSettingAsync(ClaudePathSetting, path);
            ApplyClaudePath(path);
            ClaudeCliMessage = "Saved. " + UsingClaudePathLabel(path);
        }
        catch (Exception e) { ClaudeCliMessage = "Couldn't save the claude path: " + e.Message; }
    }

    private void ApplyClaudePath(string? path)
    {
        ClaudePath = path;
        ClaudePathText = path ?? "";
    }

    private static string UsingClaudePathLabel(string? path) => path is null
        ? "Turns and summaries run claude from your PATH."
        : $"Turns and summaries run {path}.";

    // A picker whose list changed passes null until its selection is restored.
    partial void OnNewConversationModelChanged(string? value)
    {
        if (!_isLoading && value is not null)
            _ = SaveCliSettingAsync(NewConversationModelSetting, value, $"New conversations will use {value}.");
    }

    partial void OnNewConversationEffortChanged(string value)
    {
        if (!_isLoading)
            _ = SaveCliSettingAsync(NewConversationEffortSetting, NewConversationEffortOrNull,
                $"New conversations will use {(NewConversationEffortOrNull is { } effort ? effort + " effort" : "the CLI's default effort")}.");
    }

    partial void OnSummarizerModelChanged(string value)
    {
        if (!_isLoading && value is not null)
            _ = SaveCliSettingAsync(TurnSummarizer.ModelSetting, value == TurnSummarizer.DefaultModel ? null : value,
                $"Turns will be compressed with {value}.");
    }

    // Owns its errors so the property setters can fire and forget it. Null removes the setting, so the default applies.
    private async Task SaveCliSettingAsync(string key, string? value, string saved)
    {
        try
        {
            await _store.SetSettingAsync(key, value);
            ClaudeCliMessage = "Saved. " + saved;
        }
        catch (Exception e) { ClaudeCliMessage = "Couldn't save the setting: " + e.Message; }
    }

    private bool CanSaveSystemPrompt() => HasSystemPromptChanges;

    /// <summary>Saving the default removes the setting, so a later change to the built-in prompt applies.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveSystemPrompt))]
    private async Task SaveSystemPromptAsync()
    {
        var prompt = SystemPromptText.ReplaceLineEndings("\n").Trim();
        if (prompt.Length == 0)
        {
            SystemPromptMessage = "The system prompt can't be empty. Click Reset to Default to start from the built-in one.";
            return;
        }
        try
        {
            await _store.SetSettingAsync(SystemPromptSetting, prompt == ClaudeCliCodingPolicy.DefaultSystemPrompt ? null : prompt);
            ApplySystemPrompt(prompt);
            SystemPromptMessage = "Saved. Each conversation's next turn starts a new CLI session with this prompt.";
        }
        catch (Exception e) { SystemPromptMessage = "Couldn't save the system prompt: " + e.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSystemPrompt))]
    private void CancelSystemPrompt()
    {
        SystemPromptText = SystemPrompt;
        SystemPromptMessage = SystemPromptLabel(SystemPrompt);
    }

    private bool CanResetSystemPrompt() => SystemPromptText != ClaudeCliCodingPolicy.DefaultSystemPrompt;

    /// <summary>Puts the built-in prompt in the editor; Save applies it and Cancel brings back the saved one.</summary>
    [RelayCommand(CanExecute = nameof(CanResetSystemPrompt))]
    private void ResetSystemPrompt()
    {
        SystemPromptText = ClaudeCliCodingPolicy.DefaultSystemPrompt;
        SystemPromptMessage = "The built-in prompt is in the editor. Save to use it, or Cancel to keep your saved prompt.";
    }

    private void ApplySystemPrompt(string prompt)
    {
        SystemPrompt = prompt;
        SystemPromptText = prompt;
        // SystemPromptText may not have changed, so its notifications may not have run.
        OnPropertyChanged(nameof(HasSystemPromptChanges));
        SaveSystemPromptCommand.NotifyCanExecuteChanged();
        CancelSystemPromptCommand.NotifyCanExecuteChanged();
    }

    private static string SystemPromptLabel(string prompt) => prompt == ClaudeCliCodingPolicy.DefaultSystemPrompt
        ? "Using the built-in system prompt."
        : "Using your saved system prompt.";

    partial void OnRestoreLastSessionChanged(bool value)
    {
        if (!_isLoading) _ = SaveRestoreLastSessionAsync(value);
    }

    // Owns its errors so the property setter can fire and forget it.
    private async Task SaveRestoreLastSessionAsync(bool value)
    {
        try
        {
            await _store.SetSettingAsync(RestoreLastSessionSetting, value ? "true" : "false");
            Status = value ? "Saved: the last project and conversation will be restored on startup." : "Saved: startup restore is off.";
        }
        catch (Exception e) { Status = "Couldn't save the startup preference: " + e.Message; }
    }

    [RelayCommand]
    private async Task ConfigureDocxExportAsync()
    {
        if (ShowDocxExportDefaultsDialogAsync is null
            || await ShowDocxExportDefaultsDialogAsync(new DocxExportDefaultsDialogViewModel(DocxExportSettings)) is not { } settings)
            return;
        try
        {
            settings = settings.Normalize();
            await _store.SetSettingAsync(DocxExportSetting, DocxExportSettings.ToJson(settings));
            DocxExportSettings = settings;
            Status = "Saved the DOCX export defaults.";
        }
        catch (Exception e) { Status = "Couldn't save the DOCX export defaults: " + e.Message; }
    }
}

/// <summary>One of the Preferences' window sizes: edited as text, then saved, or reset to the built-in default.</summary>
public sealed partial class WindowSizePreferenceViewModel : ObservableObject
{
    private readonly ConversationStore _store;
    private readonly string _settingKey;
    private readonly string _name;

    [ObservableProperty]
    private string _widthText = "";

    [ObservableProperty]
    private string _heightText = "";

    /// <summary>The current size, or the outcome of the last Save or Reset.</summary>
    [ObservableProperty]
    private string _message = "";

    internal WindowSizePreferenceViewModel(ConversationStore store, string settingKey, string name, WindowSize defaultSize,
        WindowSize minimum)
    {
        _store = store;
        _settingKey = settingKey;
        _name = name;
        Default = defaultSize;
        Minimum = minimum;
        Apply(defaultSize);
        Message = $"Current {_name}: {Size.Label}.";
    }

    public WindowSize Default { get; }

    public WindowSize Minimum { get; }

    /// <summary>The saved size, used the next time the window opens.</summary>
    public WindowSize Size { get; private set; }

    public string DefaultWidthText => WindowSize.FormatDimension(Default.Width);

    public string DefaultHeightText => WindowSize.FormatDimension(Default.Height);

    internal async Task LoadAsync()
    {
        Apply(WindowSize.Parse(await _store.GetSettingAsync(_settingKey), Default, Minimum));
        Message = $"Current {_name}: {Size.Label}.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!WindowSize.TryParseDimension(WidthText, Minimum.Width, out var width))
        {
            Message = $"Width must be a number from {WindowSize.FormatDimension(Minimum.Width)} to {WindowSize.FormatDimension(WindowSize.MaxDimension)}.";
            return;
        }
        if (!WindowSize.TryParseDimension(HeightText, Minimum.Height, out var height))
        {
            Message = $"Height must be a number from {WindowSize.FormatDimension(Minimum.Height)} to {WindowSize.FormatDimension(WindowSize.MaxDimension)}.";
            return;
        }
        await StoreAsync(new WindowSize(width, height));
    }

    [RelayCommand]
    private Task ResetAsync() => StoreAsync(null);

    // Null removes the setting, so the built-in default applies.
    private async Task StoreAsync(WindowSize? size)
    {
        try
        {
            await _store.SetSettingAsync(_settingKey, size?.ToSetting());
            Apply(size ?? Default);
            Message = size is null ? $"Reset the {_name} to the default: {Size.Label}." : $"Saved the {_name}: {Size.Label}.";
        }
        catch (Exception e) { Message = $"Couldn't save the {_name}: {e.Message}"; }
    }

    private void Apply(WindowSize size)
    {
        Size = size;
        WidthText = WindowSize.FormatDimension(size.Width);
        HeightText = WindowSize.FormatDimension(size.Height);
    }
}
