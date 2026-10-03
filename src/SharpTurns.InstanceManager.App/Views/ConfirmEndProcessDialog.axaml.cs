using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SharpTurns.InstanceManager.App.Views;

/// <summary>
/// Modal confirmation shown before terminating a running instance's process.
/// Returns <c>true</c> when the user confirms via <see cref="ShowDialog{T}"/>.
/// </summary>
public sealed partial class ConfirmEndProcessDialog : Window
{
    public ConfirmEndProcessDialog() : this(reload: false) { }

    public ConfirmEndProcessDialog(bool reload)
    {
        InitializeComponent();
        if (reload)
        {
            Title = "Reload Instance?";
            HeadingText.Text = "Reload this SharpTurns instance?";
            ConfirmButton.Content = "Reload";
        }
    }

    /// <summary>Body text describing the instance that would be ended.</summary>
    public string? Message
    {
        set => MessageText.Text = value;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}