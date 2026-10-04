using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _isShowingDictationNotice;

    public MainWindow()
    {
        InitializeComponent();
        // Disabled controls ignore clicks, so the window watches for clicks on the ones dictation locks.
        AddHandler(PointerPressedEvent, OnPointerPressedWhileDictating, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private async void OnPointerPressedWhileDictating(object? sender, PointerPressedEventArgs e)
    {
        if (_isShowingDictationNotice || DataContext is not MainWindowViewModel { IsDictationBusy: true } viewModel) return;
        // Hit testing skips disabled controls, so look under the pointer with a filter that keeps them.
        var top = this.GetVisualsAt(e.GetPosition(this), visual => visual.IsEffectivelyVisible).FirstOrDefault();
        var locked = top?.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("dictationLocked"));
        if (locked is null || locked.IsEffectivelyEnabled) return;

        var message = viewModel.CurrentConversation?.Dictation.IsRecording == true
            ? "Dictation is recording, so switching conversations, projects, or tabs, branching, and Send are locked. " +
              "Click 🔴 Stop in the composer to finish; they unlock once the transcript is inserted."
            : "Dictation is transcribing, so switching conversations, projects, or tabs, branching, and Send are locked " +
              "until the transcript is inserted.";
        _isShowingDictationNotice = true;
        try
        {
            await PromptDialog.ForNotice("Interface Locked During Dictation", message).ShowDialog<string?>(this);
        }
        finally
        {
            _isShowingDictationNotice = false;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.ShowProjectDialogAsync = dialog => new ProjectDialog { DataContext = dialog }.ShowDialog<bool>(this);
        viewModel.ShowEditConversationDialogAsync = dialog =>
            new EditConversationDialog { DataContext = dialog }.ShowDialog<bool>(this);
        viewModel.PromptAsync = (title, message, text) =>
            new PromptDialog(title, message, text, "OK", destructive: false).ShowDialog<string?>(this);
        viewModel.ConfirmAsync = async (title, message) =>
            await new PromptDialog(title, message, null, "Delete", destructive: true).ShowDialog<string?>(this) is not null;
        viewModel.Preferences.Models.ConfirmAsync = viewModel.ConfirmAsync;
        viewModel.Preferences.McpServers.ConfirmAsync = viewModel.ConfirmAsync;
        viewModel.Preferences.Models.ChooseReplacementAsync = (title, message, choices) =>
            PromptDialog.ForChoice(title, message, choices, "Delete and Replace", destructive: true).ShowDialog<string?>(this);
        viewModel.Preferences.ShowDocxExportDefaultsDialogAsync = dialog =>
            new DocxExportDefaultsDialog { DataContext = dialog }.ShowDialog<DocxExportSettings?>(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Stop CLI processes; a turn left running is marked as interrupted on the next start.
        (DataContext as MainWindowViewModel)?.CancelRunningTurns();
        base.OnClosing(e);
    }
}
