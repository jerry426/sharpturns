using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class TurnExportDialog : Window
{
    public TurnExportDialog() => InitializeComponent();

    private TurnExportDialogViewModel? ViewModel => DataContext as TurnExportDialogViewModel;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        try
        {
            if (Clipboard is null)
            {
                viewModel.Status = "The clipboard isn't available.";
                return;
            }
            await Clipboard.SetTextAsync(viewModel.GenerateContent());
            viewModel.Status = "Copied the export to the clipboard.";
        }
        catch (Exception ex) { viewModel.Status = "Couldn't copy to the clipboard: " + ex.Message; }
    }

    private async void MacDown_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) await viewModel.OpenInMacDownAsync();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        try
        {
            var fileName = viewModel.DefaultFileName;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save {viewModel.Title}",
                SuggestedFileName = fileName,
                DefaultExtension = OperatingSystem.IsMacOS() ? Path.GetExtension(fileName).TrimStart('.') : null,
                // Avalonia 12.0.5's native macOS filter accessory can keep laying out after the picker closes, causing
                // sustained CPU use, so macOS relies on the default extension above.
                FileTypeChoices = OperatingSystem.IsMacOS() ? null : [FileType(viewModel.Format)],
            });
            if (file?.TryGetLocalPath() is { } path) await viewModel.SaveAsync(path);
        }
        catch (Exception ex) { viewModel.Status = "Couldn't save the export: " + ex.Message; }
    }

    private static FilePickerFileType FileType(TurnExportFormat format) => format switch
    {
        TurnExportFormat.Docx => new("Word Document")
        {
            Patterns = ["*.docx"], MimeTypes = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
        },
        TurnExportFormat.Text => new("Text") { Patterns = ["*.txt"], MimeTypes = ["text/plain"] },
        _ => new("Markdown") { Patterns = ["*.md", "*.markdown"], MimeTypes = ["text/markdown", "text/plain"] },
    };
}
