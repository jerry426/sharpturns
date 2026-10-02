using Avalonia.Controls;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.ShowProjectDialogAsync = dialog => new ProjectDialog { DataContext = dialog }.ShowDialog<bool>(this);
        viewModel.PromptAsync = (title, message, text) =>
            new PromptDialog(title, message, text, "OK", destructive: false).ShowDialog<string?>(this);
        viewModel.ConfirmAsync = async (title, message) =>
            await new PromptDialog(title, message, null, "Delete", destructive: true).ShowDialog<string?>(this) is not null;
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
