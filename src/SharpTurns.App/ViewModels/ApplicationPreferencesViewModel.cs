using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.Core.Persistence;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's Preferences, as in the Workbench: restoring the last session at startup, the startup, Notes, and
/// Markdown Viewer window sizes, and the DOCX export defaults. Each is saved in the settings table when it changes.
/// </summary>
public sealed partial class ApplicationPreferencesViewModel : ObservableObject
{
    private const string RestoreLastSessionSetting = "restore_last_session";
    private const string DocxExportSetting = "docx_export_settings";
    private readonly ConversationStore _store;
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreLastSessionDescription))]
    private bool _restoreLastSession = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocxExportSettingsSummary))]
    private DocxExportSettings _docxExportSettings = DocxExportSettings.Default;

    [ObservableProperty]
    private string _status = "";

    internal ApplicationPreferencesViewModel(ConversationStore store)
    {
        _store = store;
        StartupWindow = new(store, "startup_window_size", "startup window size", new(1400, 860), new(820, 560));
        NotesWindow = new(store, "notes_window_size", "Notes window size", new(980, 660), new(820, 520));
        MarkdownViewerWindow = new(store, "markdown_viewer_window_size", "Markdown Viewer window size",
            new(MarkdownViewerWindowDefaults.DefaultWidth, MarkdownViewerWindowDefaults.DefaultHeight),
            new(MarkdownViewerWindowDefaults.MinWidth, MarkdownViewerWindowDefaults.MinHeight));
    }

    public WindowSizePreferenceViewModel StartupWindow { get; }

    public WindowSizePreferenceViewModel NotesWindow { get; }

    public WindowSizePreferenceViewModel MarkdownViewerWindow { get; }

    public string RestoreLastSessionDescription => RestoreLastSession
        ? "SharpTurns will restore your last project and conversation on restart."
        : "SharpTurns will start with the first project and its most recent conversation.";

    public string DocxExportSettingsSummary => DocxExportSettings.SummaryLabel;

    /// <summary>Shows the DOCX export defaults dialog; returns the new settings, or null when canceled.</summary>
    public Func<DocxExportDefaultsDialogViewModel, Task<DocxExportSettings?>>? ShowDocxExportDefaultsDialogAsync { get; set; }

    /// <summary>Owns its errors: a preference that can't be read keeps its default, and the reason goes to the status.</summary>
    internal async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            RestoreLastSession = await _store.GetSettingAsync(RestoreLastSessionSetting) != "false";
            DocxExportSettings = DocxExportSettings.Parse(await _store.GetSettingAsync(DocxExportSetting));
            await StartupWindow.LoadAsync();
            await NotesWindow.LoadAsync();
            await MarkdownViewerWindow.LoadAsync();
        }
        catch (Exception e) { Status = "Couldn't load the preferences: " + e.Message; }
        finally { _isLoading = false; }
    }

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
