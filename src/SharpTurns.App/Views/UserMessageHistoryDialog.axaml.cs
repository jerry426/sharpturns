using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Closes with the turn the user chose to go to, or null.</summary>
public sealed partial class UserMessageHistoryDialog : Window
{
    public UserMessageHistoryDialog() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Message_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is UserMessageHistoryCardViewModel message) Close(message.Turn);
    }
}
