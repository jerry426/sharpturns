using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class ProjectDialog : Window
{
    public ProjectDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectDialogViewModel { CanSave: true }) Close(true);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectDialogViewModel viewModel) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Project's Working Directory",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.Path.LocalPath is { Length: > 0 } path) viewModel.WorkingDirectory = path;
    }
}
