using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Closes with the new settings on Save, or null when canceled.</summary>
public sealed partial class DocxExportDefaultsDialog : Window
{
    public DocxExportDefaultsDialog() => InitializeComponent();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocxExportDefaultsDialogViewModel { CanSave: true } viewModel) Close(viewModel.CreateSettings());
    }
}
