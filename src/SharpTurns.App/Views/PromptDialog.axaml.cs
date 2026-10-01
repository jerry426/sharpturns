using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SharpTurns.App.Views;

/// <summary>
/// Asks for one line of text, or for confirmation when no initial text is given.
/// Closes with the entered text ("" for a confirmation), or null when canceled.
/// </summary>
public sealed partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    public PromptDialog(string title, string message, string? initialText, string confirmText, bool destructive) : this()
    {
        Title = title;
        MessageText.Text = message;
        InputBox.IsVisible = initialText is not null;
        InputBox.Text = initialText;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Classes.Add(destructive ? "dangerActionButton" : "primaryActionButton");
        Opened += (_, _) =>
        {
            if (!InputBox.IsVisible) return;
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Confirm_Click(object? sender, RoutedEventArgs e) =>
        Close(InputBox.IsVisible ? InputBox.Text ?? "" : "");
}
