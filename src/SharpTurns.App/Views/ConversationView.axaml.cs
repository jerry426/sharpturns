using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpTurns.App.ViewModels;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.App.Views;

public sealed partial class ConversationView : UserControl
{
    private static readonly FilePickerFileType ImageFiles = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"],
        MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"],
        AppleUniformTypeIdentifiers = ["public.image"],
    };

    private const int SearchHighlightAttempts = 4;
    private const int SearchScrollAttempts = 8;
    private static readonly TimeSpan SearchApplyDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SearchScrollRetryDelay = TimeSpan.FromMilliseconds(75);

    private ConversationViewModel? _viewModel;
    // The shown turns and the items each had when last observed, so changes to what search covers re-run it.
    private readonly Dictionary<TurnViewModel, TurnItemViewModel[]> _searchedTurns = [];
    private ConversationSearchIndex? _searchIndex;
    private CancellationTokenSource? _searchBuild;
    private int _searchHighlightVersion;

    public ConversationView()
    {
        InitializeComponent();
        // Tunnel so the shortcuts run before the TextBox inserts a newline or pastes text.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.TurnContentChanged -= OnTurnContentChanged;
            _viewModel.Display.PropertyChanged -= OnDisplayChanged;
            _viewModel.ShownTurns.CollectionChanged -= OnShownTurnsChanged;
        }
        _viewModel = DataContext as ConversationViewModel;
        SyncSearchedTurns();
        CancelSearchBuild();
        _searchIndex = null;
        if (_viewModel is null) return;
        _viewModel.TurnContentChanged += OnTurnContentChanged;
        _viewModel.Display.PropertyChanged += OnDisplayChanged;
        _viewModel.ShownTurns.CollectionChanged += OnShownTurnsChanged;
        // The search carries over to this conversation; a match chosen in the last one means nothing here.
        _viewModel.Display.CurrentMatchIndex = -1;
        RefreshSearch();
        // The view is reused across conversations, so a turn running in the background keeps these hooks.
        _viewModel.ShowQuestionAsync = ShowQuestionAsync;
        _viewModel.ShowPermissionAsync = ShowPermissionAsync;
        _viewModel.ConfirmAsync = ConfirmAsync;
        _viewModel.CopyTextAsync = CopyTextAsync;
        _viewModel.ShowReplayImagesAsync = ShowReplayImagesAsync;
        _viewModel.ShowUserMessageHistoryAsync = ShowUserMessageHistoryAsync;
        _viewModel.ShowNotes = ShowNotes;
        _viewModel.ShowExportAsync = ShowExportAsync;
        _viewModel.GetScreenPoint = GetScreenPoint;
        // Show the newest turn.
        Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command && e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_viewModel?.SendCommand.CanExecute(null) == true) _viewModel.SendCommand.Execute(null);
        }
        else if (command && e.Key == Key.V)
        {
            // Clipboard formats can only be read asynchronously, so take over the paste and fall back to text.
            e.Handled = true;
            _ = PasteAsync();
        }
    }

    private async Task PasteAsync()
    {
        if (_viewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    using var png = new MemoryStream();
                    bitmap.Save(png);
                    png.Position = 0;
                    await viewModel.AddImageAsync("pasted-image.png", png);
                }
                return;
            }
            var images = (await clipboard.TryGetFilesAsync() ?? []).OfType<IStorageFile>().Where(IsImageFile).ToArray();
            if (images.Length > 0)
            {
                await AddFilesAsync(viewModel, images);
                return;
            }
        }
        catch (Exception e) { viewModel.Status = "Couldn't read the clipboard: " + e.Message; }
        Composer.Paste();
    }

    private async void AttachImage_Click(object? sender, RoutedEventArgs e) => await AttachImagesAsync(TopLevel.GetTopLevel(this));

    /// <summary>Owns its errors. The gallery passes itself as the owner so the picker opens over it.</summary>
    private async Task AttachImagesAsync(TopLevel? owner)
    {
        if (_viewModel is not { } viewModel || owner is null) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach Images",
                AllowMultiple = true,
                FileTypeFilter = [ImageFiles],
            });
            await AddFilesAsync(viewModel, files);
        }
        catch (Exception ex) { viewModel.Status = "Couldn't open the images: " + ex.Message; }
    }

    private static async Task AddFilesAsync(ConversationViewModel viewModel, IReadOnlyList<IStorageFile> files)
    {
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            await viewModel.AddImageAsync(file.Name, stream);
        }
    }

    private static bool IsImageFile(IStorageFile file) =>
        Path.GetExtension(file.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private async void Image_Click(object? sender, RoutedEventArgs e)
    {
        var image = (sender as Control)?.DataContext switch
        {
            ImageAttachmentViewModel attachment => attachment,
            TurnImageViewModel sent => sent.Preview,
            _ => null,
        };
        if (image is null) return;
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            // The composer's images open as an editable gallery of all of them; images in sent turns open alone.
            if (_viewModel is { } viewModel && viewModel.Attachments.Contains(image) && owner is not null)
            {
                await new ImagePreviewWindow(viewModel.Attachments, image, AttachImagesAsync, viewModel.RemoveAttachmentCommand.Execute)
                    .ShowDialog(owner);
                return;
            }
            var window = new ImagePreviewWindow(image);
            if (owner is not null) window.Show(owner);
            else window.Show();
        }
        catch (Exception ex)
        {
            if (_viewModel is not null) _viewModel.Status = "Couldn't open the image: " + ex.Message;
        }
    }

    private Task<string?> ShowQuestionAsync(QuestionDialogViewModel question, CancellationToken token) =>
        ShowAsync(new QuestionDialog { DataContext = question }, dialog => dialog.Answer, token);

    private Task<bool> ShowPermissionAsync(string toolName, string input, CancellationToken token) =>
        ShowAsync(new PermissionDialog(toolName, input), dialog => dialog.Allowed, token);

    private async Task<bool> ConfirmAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner
        && await new PromptDialog(title, message, null, "Delete", destructive: true).ShowDialog<string?>(owner) is not null;

    private async Task ShowReplayImagesAsync(ImagesBeingReplayedDialogViewModel images)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var turn = await new ImagesBeingReplayedDialog { DataContext = images }.ShowDialog<TurnViewModel?>(owner);
        if (turn is not null) ScrollToTurn(turn);
    }

    private async Task ShowUserMessageHistoryAsync(UserMessageHistoryDialogViewModel history)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var turn = await new UserMessageHistoryDialog { DataContext = history }.ShowDialog<TurnViewModel?>(owner);
        if (turn is not null) ScrollToTurn(turn);
    }

    private async Task ShowExportAsync(TurnExportDialogViewModel export)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) await new TurnExportDialog { DataContext = export }.ShowDialog(owner);
    }

    // One Notes window per conversation; each stays with the conversation it was opened for.
    private void ShowNotes(NotesDialogViewModel notes)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (owner.OwnedWindows.OfType<NotesDialog>()
                .FirstOrDefault(w => (w.DataContext as NotesDialogViewModel)?.ConversationId == notes.ConversationId) is { } open)
        {
            open.Activate();
            return;
        }
        new NotesDialog { DataContext = notes }.Show(owner);
    }

    // As in the Workbench, the center of the screen showing the main window.
    private PixelPoint? GetScreenPoint()
    {
        if (TopLevel.GetTopLevel(this) is not Window window || window.Screens.ScreenFromWindow(window) is not { } screen)
            return null;
        return new PixelPoint(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
    }

    /// <summary>Scrolls the turn's card to the top, first showing all turns when the Show picker leaves it out.</summary>
    private void ScrollToTurn(TurnViewModel turn)
    {
        if (_viewModel is { } viewModel && !viewModel.ShownTurns.Contains(turn))
            viewModel.Display.TurnFilterIndex = ConversationDisplayViewModel.AllTurns;
        // A card the picker just added has no container until layout runs.
        Dispatcher.UIThread.Post(() =>
        {
            if (TurnsList.ContainerFromItem(turn)?.TranslatePoint(default, TurnsList) is { } top)
                TurnsScroller.Offset = TurnsScroller.Offset.WithY(top.Y + TurnsList.Margin.Top);
        }, DispatcherPriority.Background);
    }

    private async Task CopyTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // Non-modal, so the conversation stays readable while the user decides. Canceling the token closes the dialog.
    private async Task<T> ShowAsync<TDialog, T>(TDialog dialog, Func<TDialog, T> result, CancellationToken token)
        where TDialog : Window
    {
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        await using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        if (TopLevel.GetTopLevel(this) is Window owner) dialog.Show(owner);
        else dialog.Show();
        await closed.Task;
        token.ThrowIfCancellationRequested();
        return result(dialog);
    }

    // Auto-scroll keeps new output in view; off, the reader controls the scroll. Posted so layout grows the extent first.
    // As in the Workbench, an active search owns the scroll instead.
    private void OnTurnContentChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.Display is { IsAutoScrollEnabled: true, IsSearchActive: false })
            Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
        RefreshSearch();
    }

    private void OnDisplayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel?.Display is not { } display) return;
        switch (e.PropertyName)
        {
            case nameof(ConversationDisplayViewModel.SearchQuery):
                ScheduleSearch();
                break;
            case nameof(ConversationDisplayViewModel.CurrentMatchIndex):
                ScheduleSearchHighlight();
                break;
            // As in the Workbench, turning auto-scroll on returns to the bottom, and so does a display change that reflows
            // the turns unless a search is active.
            case nameof(ConversationDisplayViewModel.IsAutoScrollEnabled) when display.IsAutoScrollEnabled:
                Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
                break;
            case nameof(ConversationDisplayViewModel.IsMarkdownRenderingEnabled)
                or nameof(ConversationDisplayViewModel.IsMonospaceFontEnabled)
                or nameof(ConversationDisplayViewModel.FontSizeOffset)
                or nameof(ConversationDisplayViewModel.BackgroundIntensity)
                or nameof(ConversationDisplayViewModel.TextIntensity):
                if (display is { IsAutoScrollEnabled: true, IsSearchActive: false })
                    Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
                // Switching between rendered Markdown and its source changes the text searched. The other
                // changes rebuild the rendered Markdown, which drops its highlights, so they're applied again.
                if (e.PropertyName == nameof(ConversationDisplayViewModel.IsMarkdownRenderingEnabled)) RefreshSearch();
                else ScheduleSearchHighlight();
                break;
        }
    }

    // ---- Search in turns, as in the Workbench ----

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel?.Display is not { } display) return;
        var command = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? display.PreviousSearchMatchCommand : display.NextSearchMatchCommand;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    // Ctrl+F or ⌘+F moves to the search box.
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnShownTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncSearchedTurns();
        RefreshSearch();
    }

    // Covers rebuilt items (compressing, View Full Turn Content) and new ones while a turn streams.
    private void OnTurnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_searchedTurns.Keys.FirstOrDefault(t => t.Items == sender) is { } turn) ObserveItems(turn);
        RefreshSearch();
    }

    // Covers text growing, a tool card opening or closing, and a question being answered.
    private void OnTurnItemChanged(object? sender, PropertyChangedEventArgs e) => RefreshSearch();

    private void SyncSearchedTurns()
    {
        var shown = _viewModel?.ShownTurns.ToHashSet() ?? [];
        foreach (var turn in _searchedTurns.Keys.Where(t => !shown.Contains(t)).ToArray())
        {
            turn.Items.CollectionChanged -= OnTurnItemsChanged;
            foreach (var item in _searchedTurns[turn]) item.PropertyChanged -= OnTurnItemChanged;
            _searchedTurns.Remove(turn);
        }
        foreach (var turn in shown.Where(t => !_searchedTurns.ContainsKey(t)))
        {
            turn.Items.CollectionChanged += OnTurnItemsChanged;
            ObserveItems(turn);
        }
    }

    private void ObserveItems(TurnViewModel turn)
    {
        if (_searchedTurns.TryGetValue(turn, out var old))
        {
            foreach (var item in old) item.PropertyChanged -= OnTurnItemChanged;
        }
        var items = turn.Items.ToArray();
        foreach (var item in items) item.PropertyChanged += OnTurnItemChanged;
        _searchedTurns[turn] = items;
    }

    private void RefreshSearch()
    {
        if (_viewModel?.Display.IsSearchActive == true) ScheduleSearch();
    }

    private void ScheduleSearch()
    {
        CancelSearchBuild();
        if (_viewModel is not { Display.IsSearchActive: true } viewModel)
        {
            _searchHighlightVersion++;
            _searchIndex = null;
            ClearSearchHighlights();
            _viewModel?.Display.SetSearchTotalMatches(0);
            return;
        }
        if (_searchIndex is not null && _searchIndex.Query != viewModel.Display.SearchQuery)
        {
            _searchIndex = null;
            ClearSearchHighlights();
        }
        _searchBuild = new CancellationTokenSource();
        _ = BuildSearchIndexAsync(viewModel, _searchBuild);
    }

    private void CancelSearchBuild()
    {
        _searchBuild?.Cancel();
        _searchBuild?.Dispose();
        _searchBuild = null;
    }

    // Owns its errors. Waits for typing and streaming to pause, then counts the matches off the UI thread.
    private async Task BuildSearchIndexAsync(ConversationViewModel viewModel, CancellationTokenSource build)
    {
        var token = build.Token;
        try
        {
            await Task.Delay(SearchApplyDelay, token);
            var display = viewModel.Display;
            var query = display.SearchQuery;
            var sources = ConversationSearchIndex.CaptureSources(viewModel.ShownTurns, display.IsMarkdownRenderingEnabled);
            var index = await Task.Run(() => ConversationSearchIndex.Build(query, sources, token), token);
            if (build != _searchBuild || viewModel != _viewModel || display.SearchQuery != query) return;
            _searchIndex = index;
            display.SetSearchTotalMatches(index.TotalMatches);
            ScheduleSearchHighlight();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search or content change replaced this one.
        }
        catch (Exception e)
        {
            viewModel.Status = "Couldn't search the turns: " + e.Message;
        }
    }

    private void ScheduleSearchHighlight()
    {
        var version = ++_searchHighlightVersion;
        if (_searchIndex is null || _viewModel?.Display is not { IsSearchActive: true } display || display.SearchQuery != _searchIndex.Query)
        {
            ClearSearchHighlights();
            return;
        }
        _ = ApplySearchHighlightsAsync(version);
    }

    // Rendered Markdown may not have laid out yet, so this retries until the active match appears, then centers it.
    private async Task ApplySearchHighlightsAsync(int version)
    {
        Control? active = null;
        for (var attempt = 0; attempt < SearchHighlightAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(SearchApplyDelay);
            var done = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _searchHighlightVersion || _searchIndex is not { } index || _viewModel?.Display is not { } display)
                    return true;
                active = ApplySearchHighlights(index, display.CurrentMatchIndex);
                return active is not null || display.CurrentMatchIndex < 0;
            }, DispatcherPriority.Background);
            if (done) break;
        }
        if (active is null) return;
        for (var attempt = 0; attempt < SearchScrollAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(SearchScrollRetryDelay);
            if (version != _searchHighlightVersion || TryCenterInTurns(active)) return;
        }
    }

    /// <summary>Highlights every match in the shown turns and returns the active match's visual, if it has rendered.</summary>
    private Control? ApplySearchHighlights(ConversationSearchIndex index, int activeMatch)
    {
        Control? active = null;
        foreach (var (control, kind) in SearchableControls())
        {
            if (control.DataContext is not { } item || !index.TryGetEntry(item, kind, out var entry))
            {
                ClearSearchHighlight(control);
                continue;
            }
            var local = activeMatch >= entry.FirstMatchIndex && activeMatch < entry.FirstMatchIndex + entry.MatchCount
                ? activeMatch - entry.FirstMatchIndex
                : -1;
            switch (control)
            {
                case MarkdownDocumentBlock document:
                    document.ApplySearchHighlight(index.Query, local);
                    if (local >= 0) active = document.GetActiveMatchContainer();
                    break;
                case HighlightableTextBlock text:
                    text.ApplySearchHighlight(index.Query, local);
                    if (local >= 0) active = text.GetActiveMatchContainer();
                    break;
            }
        }
        return active;
    }

    private void ClearSearchHighlights()
    {
        foreach (var (control, _) in SearchableControls()) ClearSearchHighlight(control);
    }

    private static void ClearSearchHighlight(Control control)
    {
        if (control is MarkdownDocumentBlock document) document.ClearSearchHighlight();
        else if (control is HighlightableTextBlock text) text.ClearSearchHighlight();
    }

    // The shown text controls on the cards; the hidden one of a response's rendered and source views is left out.
    private IEnumerable<(Control Control, ConversationSearchSegmentKind Kind)> SearchableControls()
    {
        foreach (var control in TurnsList.GetVisualDescendants().OfType<Control>())
        {
            ConversationSearchSegmentKind? kind = control.Classes switch
            {
                var c when c.Contains("searchPrompt") => ConversationSearchSegmentKind.Prompt,
                var c when c.Contains("searchText") => ConversationSearchSegmentKind.Text,
                var c when c.Contains("searchQuestion") => ConversationSearchSegmentKind.Question,
                var c when c.Contains("searchAnswer") => ConversationSearchSegmentKind.Answer,
                var c when c.Contains("searchToolInput") => ConversationSearchSegmentKind.ToolInput,
                var c when c.Contains("searchToolResult") => ConversationSearchSegmentKind.ToolResult,
                _ => null,
            };
            if (kind is { } found && control.IsEffectivelyVisible) yield return (control, found);
        }
    }

    // Puts the match in the middle of the turn list, as in the Workbench.
    private bool TryCenterInTurns(Control match)
    {
        if (!match.IsEffectivelyVisible || match.Bounds.Width <= 0 || match.Bounds.Height <= 0) return false;
        if (match.TranslatePoint(new Point(match.Bounds.Width / 2, match.Bounds.Height / 2), TurnsScroller) is not { } center)
            return false;
        // The point is relative to the viewport, so adding the offset gives its position in the content.
        var target = center.Y + TurnsScroller.Offset.Y - TurnsScroller.Viewport.Height / 2;
        var max = Math.Max(0, TurnsScroller.Extent.Height - TurnsScroller.Viewport.Height);
        TurnsScroller.Offset = TurnsScroller.Offset.WithY(Math.Clamp(target, 0, max));
        return true;
    }
}
