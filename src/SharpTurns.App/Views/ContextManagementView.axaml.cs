using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class ContextManagementView : UserControl
{
    public ContextManagementView()
    {
        InitializeComponent();
        // Tunnel, so a row's check box click can select a range before the check box toggles itself.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        if ((source as CheckBox ?? source?.FindAncestorOfType<CheckBox>()) is not { IsEnabled: true, DataContext: TurnViewModel turn } checkBox
            || DataContext is not ConversationViewModel viewModel
            || !e.GetCurrentPoint(checkBox).Properties.IsLeftButtonPressed)
            return;
        viewModel.SelectContextTurn(turn, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }
}
