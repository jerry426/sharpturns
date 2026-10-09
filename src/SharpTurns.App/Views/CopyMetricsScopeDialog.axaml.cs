using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Asks what Copy Metrics copies. Closes with the chosen scope, or null when canceled.</summary>
public sealed partial class CopyMetricsScopeDialog : Window
{
    public CopyMetricsScopeDialog() => InitializeComponent();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void CurrentTurn_Click(object? sender, RoutedEventArgs e) => Close(MetricsCopyScope.Turn);

    private void EntireConversation_Click(object? sender, RoutedEventArgs e) => Close(MetricsCopyScope.Conversation);
}
