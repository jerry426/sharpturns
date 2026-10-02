using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SharpTurns.App.Views;

/// <summary>
/// Asks for one line of text, for a choice from a list, or for confirmation when neither is given.
/// Closes with the entered text or chosen item ("" for a confirmation), or null when canceled.
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

    /// <summary>Asks for one of the choices; the first starts selected.</summary>
    public static PromptDialog ForChoice(string title, string message, IReadOnlyList<string> choices, string confirmText,
        bool destructive)
    {
        var dialog = new PromptDialog(title, message, null, confirmText, destructive);
        dialog.ChoiceBox.IsVisible = true;
        dialog.ChoiceBox.ItemsSource = choices;
        dialog.ChoiceBox.SelectedIndex = 0;
        return dialog;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Confirm_Click(object? sender, RoutedEventArgs e) =>
        Close(InputBox.IsVisible ? InputBox.Text ?? "" : ChoiceBox.IsVisible ? ChoiceBox.SelectedItem as string : "");
}
