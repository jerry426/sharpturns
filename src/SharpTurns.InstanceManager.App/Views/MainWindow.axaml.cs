using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SharpTurns.InstanceManager.App.ViewModels;

namespace SharpTurns.InstanceManager.App.Views;

public sealed partial class MainWindow : Window
{
    // Drag-to-reorder state. Press on a project or instance card starts tracking; movement past a threshold begins a
    // drag (reorder on release). Project cards reorder in the project list; instance cards reorder within their project
    // card. A press+release without movement on an instance card is treated as a click and focuses that instance instead.
    private object? _dragItem;
    private Border? _dragCard;
    private ItemsControl? _dragList;
    private Point _dragStart;
    private bool _dragging;
    private const double DragThreshold = 5.0;

    public MainWindow()
    {
        InitializeComponent();
    }

    // Each list sits in a Grid alongside its drop indicator (see MainWindow.axaml).
    private static (Grid Overlay, Border Indicator)? OverlayForList(ItemsControl list) =>
        list.Parent is Grid overlay
        && overlay.Children.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("dropIndicator")) is { } indicator
            ? (overlay, indicator)
            : null;

    private static void ShowDropIndicator(ItemsControl list, Point pointerInPanel)
    {
        if (OverlayForList(list) is not (var overlay, var indicator))
        {
            return;
        }

        if (list.ItemsPanelRoot is not { } panel)
        {
            indicator.IsVisible = false;
            return;
        }

        var insertBefore = InsertionIndexFor(panel, pointerInPanel);

        Control? anchor;
        bool atBottom;
        if (insertBefore < panel.Children.Count)
        {
            anchor = list.ContainerFromIndex(insertBefore) as Control;
            atBottom = false;
        }
        else if (panel.Children.Count > 0)
        {
            anchor = list.ContainerFromIndex(panel.Children.Count - 1) as Control;
            atBottom = true;
        }
        else
        {
            indicator.IsVisible = false;
            return;
        }

        if (anchor is null)
        {
            indicator.IsVisible = false;
            return;
        }

        var y = atBottom
            ? anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), overlay)?.Y ?? double.NaN
            : anchor.TranslatePoint(new Point(0, 0), overlay)?.Y ?? double.NaN;

        if (double.IsNaN(y))
        {
            indicator.IsVisible = false;
            return;
        }

        // Clamp to the overlay so a line scrolled just out of view still renders at the edge rather than disappearing.
        var maxY = overlay.Bounds.Height - indicator.Bounds.Height;
        if (maxY > 0)
        {
            y = Math.Max(0, Math.Min(y, maxY));
        }

        indicator.Margin = new Thickness(0, y, 0, 0);
        indicator.IsVisible = true;
    }

    private static void HideDropIndicator(ItemsControl list)
    {
        if (OverlayForList(list) is (_, var indicator))
        {
            indicator.IsVisible = false;
        }
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: InstanceCardViewModel } card)
        {
            BeginTracking(card, e);
        }
    }

    private void Project_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: ProjectGroupViewModel } card)
        {
            BeginTracking(card, e);
        }
    }

    private void BeginTracking(Border card, PointerPressedEventArgs e)
    {
        // Don't start a drag when the press lands on a nested button (Launch, the collapse toggle, End Process, Reload).
        if (IsOverButton(e.Source) || card.FindAncestorOfType<ItemsControl>() is not { } list)
        {
            return;
        }

        // Keep an instance card's press from also starting its project card's drag.
        e.Handled = true;
        _dragCard = card;
        _dragItem = card.DataContext;
        _dragList = list;
        _dragStart = e.GetPosition(card);
        _dragging = false;
        e.Pointer.Capture(card);
    }

    private void Drag_PointerMoved(object? sender, PointerEventArgs e)
    {
        // Moves bubble from a captured instance card to its project card; only the tracked card handles them.
        if (_dragCard is null || !ReferenceEquals(sender, _dragCard) || _dragList is null)
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
        if (_dragList.ItemsPanelRoot is { } panel)
        {
            ShowDropIndicator(_dragList, e.GetPosition(panel));
        }
    }

    private void Drag_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard is null || !ReferenceEquals(sender, _dragCard) || _dragItem is null || _dragList is null)
        {
            return;
        }

        var card = _dragCard;
        var item = _dragItem;
        var list = _dragList;
        _dragCard = null;
        _dragItem = null;
        _dragList = null;

        try
        {
            e.Pointer.Capture(null);
        }
        catch
        {
            // Best-effort release.
        }

        HideDropIndicator(list);
        if (_dragging)
        {
            _dragging = false;
            card.Opacity = 1;

            if (list.ItemsPanelRoot is { } panel && DataContext is InstanceManagerViewModel vm)
            {
                var insertBefore = InsertionIndexFor(panel, e.GetPosition(panel));
                if (item is ProjectGroupViewModel group)
                {
                    vm.MoveProject(group, insertBefore);
                }
                else if (item is InstanceCardViewModel instance)
                {
                    vm.MoveInstance(instance, insertBefore);
                }
            }
        }
        else if (item is InstanceCardViewModel instance)
        {
            // Plain click → focus that instance.
            instance.FocusCommand.Execute(null);
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
