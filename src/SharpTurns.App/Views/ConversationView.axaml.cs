using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class ConversationView : UserControl
{
    private ConversationViewModel? _viewModel;

    public ConversationView()
    {
        InitializeComponent();
        // Tunnel so the shortcut sends before the TextBox inserts a newline.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.TurnContentChanged -= OnTurnContentChanged;
        _viewModel = DataContext as ConversationViewModel;
        if (_viewModel is null) return;
        _viewModel.TurnContentChanged += OnTurnContentChanged;
        // The view is reused across conversations; show the newest turn.
        Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        e.Handled = true;
        if (_viewModel?.SendCommand.CanExecute(null) == true) _viewModel.SendCommand.Execute(null);
    }

    private void OnTurnContentChanged(object? sender, EventArgs e)
    {
        // Follow new output only when the reader is already at the bottom; this runs before layout grows the extent.
        var atBottom = TurnsScroller.Offset.Y >= TurnsScroller.Extent.Height - TurnsScroller.Viewport.Height - 48;
        if (atBottom) Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
    }
}
