using System.Text;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpTurns.Markdown.Rendering;
using SharpTurns.MarkdownViewer.App.Services;
using SharpTurns.MarkdownViewer.App.ViewModels;
using LiveMarkdown.Avalonia;

namespace SharpTurns.MarkdownViewer.App.Views;

public sealed partial class MarkdownViewerWindow : Window
{
    private static readonly TimeSpan SearchApplyDelay = TimeSpan.FromMilliseconds(40);
    private const int SearchApplyAttempts = 4;
    private const int SearchScrollAttempts = 3;

    private readonly string? _initialFilePath;
    private readonly InitialWindowActionGate _initialActionGate;
    private readonly ViewerFileTargetState _fileTargetState;
    private IStorageFile? _currentFile;
    private int _searchApplyVersion;
    private bool _appearanceControlsOnSecondRow;
    private double _appearanceControlsRequiredWidth;
    private bool _searchCardLayoutUpdatePending;
    private bool _fileTreeNavigationPending;
    private bool _documentTreeSyncPending;

    public MarkdownViewerWindow()
        : this(new MarkdownViewerViewModel(null), null)
    {
    }

    internal MarkdownViewerWindow(
        MarkdownViewerViewModel viewModel,
        string? initialFilePath,
        double? windowWidth = null,
        double? windowHeight = null)
    {
        _initialFilePath = initialFilePath;
        _initialActionGate = new InitialWindowActionGate(requireActivation: false);
        _fileTargetState = new ViewerFileTargetState(initialFilePath);
        InitializeComponent();
        if (windowWidth is { } width)
        {
            Width = width;
        }

        if (windowHeight is { } height)
        {
            Height = height;
        }

        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Appearance.PropertyChanged += OnAppearancePropertyChanged;
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        RenderedDocument.AddHandler(MarkdownContentBlock.PreviewLinkClickEvent, OnMarkdownLinkClick);
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnFileTreeItemExpanded, RoutingStrategies.Bubble);
    }

    internal event EventHandler? NewWindowRequested;

    internal bool CanReceiveActivatedFile =>
        string.IsNullOrWhiteSpace(_initialFilePath) &&
        DataContext is MarkdownViewerViewModel { CanRefresh: false };

    internal bool IsShowingOrOpeningFile(string filePath) =>
        DataContext is MarkdownViewerViewModel viewModel &&
        _fileTargetState.Matches(filePath, viewModel.FilePath);

    internal Task LoadActivatedFileAsync(string filePath) => LoadStartupFileAsync(filePath);

    private void OnOpened(object? sender, EventArgs e)
    {
        ScheduleSearchCardLayoutUpdate();

        if (_initialActionGate.MarkOpened())
        {
            ScheduleInitialAction();
        }
    }

    private void ScheduleInitialAction() =>
        Dispatcher.UIThread.Post(HandleInitialActionAsync, DispatcherPriority.Background);

    private async void HandleInitialActionAsync()
    {
        if (DataContext is MarkdownViewerViewModel viewModel)
        {
            await viewModel.FileBrowser.RefreshAsync(viewModel.PickerDirectory);
        }

        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            await LoadStartupFileAsync(_initialFilePath);
        }
    }

    private async void OnFileTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: MarkdownViewerFileBrowserNode node } && node.IsDirectory)
        {
            await LoadFileTreeChildrenAsync(node);
        }
    }

    private async Task LoadFileTreeChildrenAsync(MarkdownViewerFileBrowserNode node)
    {
        if (DataContext is MarkdownViewerViewModel viewModel)
        {
            await viewModel.FileBrowser.LoadChildrenAsync(node);
        }
    }

    private void NewWindow_Click(object? sender, RoutedEventArgs e) =>
        NewWindowRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs e)
    {
        RenderedDocument.RemoveHandler(MarkdownContentBlock.PreviewLinkClickEvent, OnMarkdownLinkClick);
        FileTree.RemoveHandler(TreeViewItem.ExpandedEvent, OnFileTreeItemExpanded);
        if (DataContext is MarkdownViewerViewModel viewModel)
        {
            viewModel.CancelRemoteLoad();
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Appearance.PropertyChanged -= OnAppearancePropertyChanged;
        }
    }

    private void OnAppearancePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ScheduleSearchApply();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkdownViewerViewModel.FilePath))
        {
            RenderedScrollViewer.Offset = default;
            RawScrollViewer.Offset = default;
            SynchronizeDocumentTreeAsync();
        }

        if (e.PropertyName is nameof(MarkdownViewerViewModel.SearchQuery)
            or nameof(MarkdownViewerViewModel.CurrentMatchIndex)
            or nameof(MarkdownViewerViewModel.DisplayMode)
            or nameof(MarkdownViewerViewModel.Markdown))
        {
            ScheduleSearchApply();
        }

        if (e.PropertyName is nameof(MarkdownViewerViewModel.SearchQuery)
            or nameof(MarkdownViewerViewModel.IsSearchActive)
            or nameof(MarkdownViewerViewModel.SearchMatchStatusLabel))
        {
            ScheduleSearchCardLayoutUpdate();
        }
    }

    private async void SynchronizeDocumentTreeAsync()
    {
        if (_documentTreeSyncPending || DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        _documentTreeSyncPending = true;
        try
        {
            string path;
            do
            {
                path = viewModel.FilePath;
                if (viewModel.IsRemoteDocument)
                {
                    viewModel.FileBrowser.SelectedNode = null;
                }
                else
                {
                    await viewModel.FileBrowser.EnsureFileVisibleAsync(path);
                }
            }
            while (path != viewModel.FilePath);
        }
        catch (Exception ex)
        {
            viewModel.SetStatus($"Unable to update the Markdown file tree: {ex.Message}");
        }
        finally
        {
            _documentTreeSyncPending = false;
        }
    }

    private async void OnMarkdownLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.Handled || DataContext is not MarkdownViewerViewModel viewModel ||
            viewModel.ResolveMarkdownLink(e.HRef) is not { } target)
        {
            return;
        }

        // Claim the event before awaiting so the renderer cannot also launch a browser.
        e.Handled = true;
        if (target.IsFile)
        {
            await LoadTreeFileAsync(target.LocalPath);
        }
        else
        {
            await viewModel.LoadRemoteDocumentAsync(target);
        }
    }

    private void SearchCardLayout_SizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateSearchCardLayout();

    private void ScheduleSearchCardLayoutUpdate()
    {
        if (_searchCardLayoutUpdatePending)
        {
            return;
        }

        _searchCardLayoutUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _searchCardLayoutUpdatePending = false;
            UpdateSearchCardLayout();
        }, DispatcherPriority.Background);
    }

    private void UpdateSearchCardLayout()
    {
        var availableWidth = SearchCardLayout.Bounds.Width;
        var searchControlsWidth = SearchControls.DesiredSize.Width;
        var measuredAppearanceWidth = AppearanceControls.DesiredSize.Width;
        if (availableWidth <= 0 || searchControlsWidth <= 0 || measuredAppearanceWidth <= 0)
        {
            return;
        }

        _appearanceControlsRequiredWidth = Math.Max(
            _appearanceControlsRequiredWidth,
            measuredAppearanceWidth);

        var shouldWrap = SearchCardLayoutPolicy.ShouldWrapAppearanceControls(
            availableWidth,
            searchControlsWidth,
            _appearanceControlsRequiredWidth,
            SearchCardLayout.ColumnSpacing);
        if (shouldWrap == _appearanceControlsOnSecondRow)
        {
            return;
        }

        _appearanceControlsOnSecondRow = shouldWrap;
        Grid.SetRow(AppearanceControls, shouldWrap ? 1 : 0);
        Grid.SetColumn(AppearanceControls, shouldWrap ? 0 : 1);
        Grid.SetColumnSpan(AppearanceControls, shouldWrap ? 2 : 1);
        AppearanceControls.HorizontalAlignment = shouldWrap
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Right;
    }

    private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Return)
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? viewModel.PreviousSearchMatchCommand
                : viewModel.NextSearchMatchCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && viewModel.ClearSearchCommand.CanExecute(null))
        {
            viewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right &&
            DataContext is MarkdownViewerViewModel navigation)
        {
            var command = e.Key == Key.Left ? navigation.BackCommand : navigation.ForwardCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
            e.Handled = true;
            return;
        }

        if (IsPlatformCopyGesture(e) && IsEventWithin(e.Source, RawDocument) && RawDocument.CanCopy)
        {
            e.Handled = await RawDocument.CopySelectionAsync();
            return;
        }

        if (IsPlatformCopyGesture(e) && FindMarkdownRenderer(e.Source) is { CanCopy: true } renderer)
        {
            e.Handled = await renderer.CopySelectionWithFormattingAsync();
            return;
        }

        if (e.Key == Key.F &&
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape &&
            DataContext is MarkdownViewerViewModel viewModel &&
            viewModel.ClearSearchCommand.CanExecute(null))
        {
            viewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ReanchorFileTree_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MarkdownViewerViewModel viewModel)
        {
            await viewModel.FileBrowser.ReanchorAtSelectedDirectoryAsync();
        }
    }

    private async void RefreshFileTree_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        var directory = viewModel.FileBrowser.RootDirectory ?? viewModel.PickerDirectory;
        await viewModel.FileBrowser.RefreshAsync(directory);
        if (!viewModel.IsRemoteDocument && !string.IsNullOrWhiteSpace(viewModel.FilePath))
        {
            await viewModel.FileBrowser.EnsureFileVisibleAsync(viewModel.FilePath);
        }
    }

    private async void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_fileTreeNavigationPending || _documentTreeSyncPending ||
            DataContext is not MarkdownViewerViewModel viewModel ||
            FileTree.SelectedItem is not MarkdownViewerFileBrowserNode node)
        {
            return;
        }

        if (node.IsParentDirectory)
        {
            _fileTreeNavigationPending = true;
            try
            {
                await viewModel.FileBrowser.RefreshAsync(node.FullPath);
                if (!viewModel.IsRemoteDocument && !string.IsNullOrWhiteSpace(viewModel.FilePath))
                {
                    await viewModel.FileBrowser.EnsureFileVisibleAsync(viewModel.FilePath);
                }
            }
            finally
            {
                _fileTreeNavigationPending = false;
            }

            return;
        }

        if (!node.IsMarkdownFile || PathsEqual(node.FullPath, viewModel.FilePath))
        {
            return;
        }

        await LoadTreeFileAsync(node.FullPath);
    }

    private async Task LoadTreeFileAsync(string filePath, bool isRefresh = false)
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        var version = viewModel.BeginLocalDocumentLoad();
        try
        {
            var file = await StorageProvider.TryGetFileFromPathAsync(filePath);
            if (!viewModel.IsDocumentLoadCurrent(version))
            {
                return;
            }
            if (file is null)
            {
                SetStatus($"Unable to access the selected Markdown file: {filePath}");
                return;
            }

            await LoadFileAsync(file, isRefresh, version);
        }
        catch (Exception ex)
        {
            if (viewModel.IsDocumentLoadCurrent(version))
            {
                SetStatus($"Unable to access the selected Markdown file: {ex.Message}");
            }
        }
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e) =>
        await OpenFileAsync();

    private async Task OpenFileAsync()
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        IStorageFolder? suggestedFolder = null;
        if (!string.IsNullOrWhiteSpace(viewModel.PickerDirectory) &&
            Directory.Exists(viewModel.PickerDirectory))
        {
            suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(viewModel.PickerDirectory);
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Markdown Documentation",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedFolder,
            // Avalonia 12.0.5's native macOS filter accessory can keep laying out after
            // the picker closes, causing sustained CPU use.
            FileTypeFilter = OperatingSystem.IsMacOS() ? null : [CreateMarkdownFileType()],
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            if (!viewModel.CanRefresh)
            {
                viewModel.SetStatus("Choose a Markdown file to begin.");
            }

            return;
        }

        await LoadFileAsync(file, isRefresh: false);
    }

    internal static FilePickerFileType CreateMarkdownFileType() =>
        new("Markdown")
        {
            Patterns = ["*.md", "*.markdown"],
            AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
            MimeTypes = ["text/markdown", "text/plain"],
        };

    private async Task LoadStartupFileAsync(string filePath)
    {
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && MarkdownDocumentLink.IsRemoteMarkdown(uri))
        {
            if (DataContext is MarkdownViewerViewModel viewModel)
            {
                await viewModel.LoadRemoteDocumentAsync(uri);
            }

            return;
        }

        _fileTargetState.MarkOpening(filePath);
        try
        {
            await LoadTreeFileAsync(filePath);
        }
        finally
        {
            _fileTargetState.MarkOpeningCompleted(filePath);
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MarkdownViewerViewModel { RemoteDocumentUri: { } uri } viewModel)
        {
            await viewModel.LoadRemoteDocumentAsync(uri);
        }
        else if (DataContext is MarkdownViewerViewModel local && local.CanRefresh)
        {
            if (_currentFile is not null && PathsEqual(_currentFile.Path.LocalPath, local.FilePath))
            {
                await LoadFileAsync(_currentFile, isRefresh: true);
            }
            else
            {
                await LoadTreeFileAsync(local.FilePath, isRefresh: true);
            }
        }
    }

    private async Task LoadFileAsync(IStorageFile file, bool isRefresh, int? loadVersion = null)
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return;
        }

        var version = loadVersion ?? viewModel.BeginLocalDocumentLoad();
        try
        {
            if (isRefresh)
            {
                viewModel.SetStatus($"Refreshing {file.Name}…");
            }

            await using var stream = await file.OpenReadAsync();
            var streamByteSize = stream.CanSeek ? (ulong)stream.Length : (ulong?)null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var markdown = await reader.ReadToEndAsync();
            var fileProperties = await TryGetFilePropertiesAsync(file);
            if (!viewModel.IsDocumentLoadCurrent(version))
            {
                return;
            }
            viewModel.SetDocument(
                file.Name,
                file.Path.LocalPath,
                markdown,
                fileProperties.ModifiedAt,
                fileProperties.ByteSize ?? streamByteSize);
            _currentFile = file;
        }
        catch (Exception ex)
        {
            var operation = isRefresh ? "refresh" : "read";
            if (viewModel.IsDocumentLoadCurrent(version))
            {
                viewModel.SetStatus($"Unable to {operation} the selected file: {ex.Message}");
            }
        }
    }

    private static async Task<(DateTimeOffset? ModifiedAt, ulong? ByteSize)> TryGetFilePropertiesAsync(IStorageFile file)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            return (properties.DateModified, properties.Size);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private void SetStatus(string message)
    {
        if (DataContext is MarkdownViewerViewModel viewModel)
        {
            viewModel.SetStatus(message);
        }
    }

    private void ScheduleSearchApply()
    {
        var version = ++_searchApplyVersion;
        _ = ApplySearchAsync(version);
    }

    private async Task ApplySearchAsync(int version)
    {
        await Task.Delay(SearchApplyDelay).ConfigureAwait(false);

        Control? activeMatch = null;
        for (var attempt = 0; attempt < SearchApplyAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(SearchApplyDelay).ConfigureAwait(false);
            }

            activeMatch = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _searchApplyVersion)
                {
                    return null;
                }

                return ApplySearchOnce();
            });

            if (activeMatch is not null || version != _searchApplyVersion)
            {
                break;
            }
        }

        if (activeMatch is not null && version == _searchApplyVersion)
        {
            await CenterActiveMatchAsync(version, activeMatch).ConfigureAwait(false);
        }
    }

    private Control? ApplySearchOnce()
    {
        if (DataContext is not MarkdownViewerViewModel viewModel)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(viewModel.SearchQuery))
        {
            RenderedDocument.ClearSearchHighlight();
            RawDocument.ClearSearchHighlight();
            viewModel.SetSearchTotalMatches(0);
            return null;
        }

        var activeView = viewModel.IsRenderedMode
            ? (ISearchableDocumentView)RenderedDocument
            : RawDocument;
        var inactiveView = viewModel.IsRenderedMode
            ? (ISearchableDocumentView)RawDocument
            : RenderedDocument;
        inactiveView.ClearSearchHighlight();

        var matchCount = activeView.GetSearchMatchCount(viewModel.SearchQuery);
        viewModel.SetSearchTotalMatches(matchCount);
        activeView.ApplySearchHighlight(viewModel.SearchQuery, viewModel.CurrentMatchIndex);
        return activeView.GetActiveMatchContainer();
    }

    private async Task CenterActiveMatchAsync(int version, Control match)
    {
        for (var attempt = 0; attempt < SearchScrollAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(SearchApplyDelay).ConfigureAwait(false);
            }

            var centered = await Dispatcher.UIThread.InvokeAsync(() =>
                version != _searchApplyVersion || TryCenterActiveMatch(match));
            if (centered)
            {
                return;
            }
        }
    }

    private bool TryCenterActiveMatch(Control match)
    {
        if (DataContext is not MarkdownViewerViewModel viewModel ||
            !match.IsEffectivelyVisible ||
            match.Bounds.Width <= 0 ||
            match.Bounds.Height <= 0)
        {
            return false;
        }

        var scrollViewer = viewModel.IsRenderedMode ? RenderedScrollViewer : RawScrollViewer;
        var center = new Point(match.Bounds.Width / 2, match.Bounds.Height / 2);
        var position = match.TranslatePoint(center, scrollViewer);
        if (!position.HasValue)
        {
            return false;
        }

        var absoluteY = position.Value.Y + scrollViewer.Offset.Y;
        var targetY = absoluteY - scrollViewer.Viewport.Height / 2;
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(targetY, 0, maxOffset));
        return true;
    }

    private static bool IsPlatformCopyGesture(KeyEventArgs e) =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(gesture => gesture.Matches(e)) == true;

    private static bool IsEventWithin(object? eventSource, Visual ancestor) =>
        eventSource is Visual visual &&
        (ReferenceEquals(visual, ancestor) || visual.GetVisualAncestors().Contains(ancestor));

    private static MarkdownContentBlock? FindMarkdownRenderer(object? eventSource)
    {
        if (eventSource is MarkdownContentBlock renderer)
        {
            return renderer;
        }

        return (eventSource as Visual)?.GetVisualAncestors().OfType<MarkdownContentBlock>().FirstOrDefault();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
