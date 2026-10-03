using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SharpTurns.InstanceManager.App.ViewModels;

namespace SharpTurns.InstanceManager.App.Views;

public sealed partial class MainWindow : Window
{
    // Drag-to-reorder state. Press on a card body starts tracking; movement past a threshold begins a drag (reorder on
    // release). A press+release without movement is treated as a click and focuses that instance instead.
    private InstanceCardViewModel? _dragItem;
    private Border? _dragCard;
    private Point _dragStart;
    private bool _dragging;
    private const double DragThreshold = 5.0;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ShowDropIndicator(Point pointerInPanel)
    {
        if (InstanceList.ItemsPanelRoot is not { } panel)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        var insertBefore = InsertionIndexFor(panel, pointerInPanel);

        Control? anchor;
        bool atBottom;
        if (insertBefore < panel.Children.Count)
        {
            anchor = InstanceList.ContainerFromIndex(insertBefore) as Control;
            atBottom = false;
        }
        else if (panel.Children.Count > 0)
        {
            anchor = InstanceList.ContainerFromIndex(panel.Children.Count - 1) as Control;
            atBottom = true;
        }
        else
        {
            DropIndicator.IsVisible = false;
            return;
        }

        if (anchor is null)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        var y = atBottom
            ? anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), ListOverlay)?.Y ?? double.NaN
            : anchor.TranslatePoint(new Point(0, 0), ListOverlay)?.Y ?? double.NaN;

        if (double.IsNaN(y))
        {
            DropIndicator.IsVisible = false;
            return;
        }

        // Clamp to the overlay so a line scrolled just out of view still renders at the edge rather than disappearing.
        var maxY = ListOverlay.Bounds.Height - DropIndicator.Bounds.Height;
        if (maxY > 0)
        {
            y = Math.Max(0, Math.Min(y, maxY));
        }

        DropIndicator.Margin = new Thickness(0, y, 0, 0);
        DropIndicator.IsVisible = true;
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't start a drag when the press lands on a nested button (End Process or Reload).
        if (IsOverButton(e.Source))
        {
            return;
        }

        if (sender is not Border card || card.DataContext is not InstanceCardViewModel vm)
        {
            return;
        }

        _dragCard = card;
        _dragItem = vm;
        _dragStart = e.GetPosition(card);
        _dragging = false;
        e.Pointer.Capture(card);
    }

    private void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard is null)
        {
            return;
        }

        if (!_dragging)
        {
            var delta = e.GetPosition(_dragCard) - _dragStart;
            if (delta.X * delta.X + delta.Y * delta.Y <= DragThreshold * DragThreshold)
            {
                return;
            }

            _dragging = true;
            _dragCard.Opacity = 0.45;
        }

        // Show a live drop indicator line at the insertion point.
        if (InstanceList.ItemsPanelRoot is { } panel)
        {
            ShowDropIndicator(e.GetPosition(panel));
        }
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard is null || _dragItem is null)
        {
            return;
        }

        var card = _dragCard;
        var item = _dragItem;
        _dragCard = null;
        _dragItem = null;

        try
        {
            e.Pointer.Capture(null);
        }
        catch
        {
            // Best-effort release.
        }

        DropIndicator.IsVisible = false;
        if (_dragging)
        {
            _dragging = false;
            card.Opacity = 1;

            if (InstanceList.ItemsPanelRoot is { } panel && DataContext is InstanceManagerViewModel vm)
            {
                vm.MoveInstance(item.Id, InsertionIndexFor(panel, e.GetPosition(panel)));
            }
        }
        else
        {
            // Plain click → focus that instance.
            item.FocusCommand.Execute(null);
        }
    }

    private static int InsertionIndexFor(Panel panel, Point pos)
    {
        for (var i = 0; i < panel.Children.Count; i++)
        {
            var bounds = panel.Children[i].Bounds;
            if (pos.Y < bounds.Y + bounds.Height / 2)
            {
                return i;
            }
        }

        return panel.Children.Count;
    }

    // The card itself is a Border, so any Button ancestor is one of its buttons (or their content); pressing there
    // should not start a drag.
    private static bool IsOverButton(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) is not null;
}
