using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Non-modal, so the conversation stays usable while the notes are open.</summary>
public sealed partial class NotesDialog : Window
{
    public NotesDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is NotesDialogViewModel notes) notes.ConfirmAsync = ConfirmAsync;
    }

    private async Task<bool> ConfirmAsync(string title, string message) =>
        await new PromptDialog(title, message, null, "Delete Note", destructive: true).ShowDialog<string?>(this) is not null;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
