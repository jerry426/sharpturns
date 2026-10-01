using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;

namespace SharpTurns.App.Views;

/// <summary>Asks whether a tool request the CLI escalated may run. Closing the window denies it.</summary>
public sealed partial class PermissionDialog : Window
{
    public PermissionDialog() => InitializeComponent();

    public PermissionDialog(string toolName, string input) : this()
    {
        // Reuse the tool card's summary and readable input.
        var tool = new ToolItemViewModel(new ToolCallRecord("", toolName, input, null, "", false));
        HeadingText.Text = string.IsNullOrEmpty(tool.Summary) ? $"Allow {toolName}?" : $"Allow {toolName}: {tool.Summary}?";
        InputText.Text = tool.InputText;
    }

    public bool Allowed { get; private set; }

    private void Allow_Click(object? sender, RoutedEventArgs e)
    {
        Allowed = true;
        Close();
    }

    private void Deny_Click(object? sender, RoutedEventArgs e) => Close();
}
