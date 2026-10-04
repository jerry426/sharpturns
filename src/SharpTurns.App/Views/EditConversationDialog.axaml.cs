using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class EditConversationDialog : Window
{
    public EditConversationDialog()
    {
        InitializeComponent();
        Opened += (_, _) => TitleBox.Focus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditConversationDialogViewModel { CanSave: true }) Close(true);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditConversationDialogViewModel viewModel) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Conversation's Workspace",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.Path.LocalPath is { Length: > 0 } path) viewModel.WorkingDirectory = path;
    }

    private async void AddContextFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditConversationDialogViewModel viewModel) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Conversation Context Files",
            AllowMultiple = true,
        });
        foreach (var file in files)
            if (file.Path.LocalPath is { Length: > 0 } path) viewModel.TryAddContextFile(path);
    }
}
