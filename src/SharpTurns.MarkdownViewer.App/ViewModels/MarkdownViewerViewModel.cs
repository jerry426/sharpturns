using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Markdown.Rendering;
using SharpTurns.MarkdownViewer.App.Services;

namespace SharpTurns.MarkdownViewer.App.ViewModels;

public sealed partial class MarkdownViewerViewModel : ObservableObject
{
    private string _fileName = string.Empty;
    private string _filePath = string.Empty;
    private string _markdown = string.Empty;
    private string? _pickerDirectory;
    private MarkdownViewerDisplayMode _displayMode;
    private string _searchQuery = string.Empty;
    private int _currentMatchIndex = -1;
    private int _totalSearchMatches;
    private string _statusMessage = "Choose a Markdown file to begin.";
    private readonly RemoteMarkdownLoader _remoteLoader;
    private CancellationTokenSource? _remoteLoadCancellation;
    private int _documentLoadVersion;
    private readonly List<DocumentSnapshot> _history = [];
    private int _historyIndex = -1;

    private sealed record DocumentSnapshot(
        string FileName, string FilePath, string Markdown,
        DateTimeOffset? ModifiedAt, ulong? ByteSize, Uri? RemoteUri);

    public MarkdownViewerViewModel(string? initialDirectory)
        : this(initialDirectory, RemoteMarkdownLoader.Shared)
    {
    }

    internal MarkdownViewerViewModel(string? initialDirectory, RemoteMarkdownLoader remoteLoader)
    {
        _remoteLoader = remoteLoader;
        _pickerDirectory = NormalizeDirectory(initialDirectory);
        Appearance = new MarkdownViewerAppearanceViewModel();
        FileBrowser = new MarkdownViewerFileBrowserViewModel();
        BackCommand = new RelayCommand(() => NavigateHistory(-1), () => _historyIndex > 0);
        ForwardCommand = new RelayCommand(() => NavigateHistory(1), () => _historyIndex + 1 < _history.Count);
        NextSearchMatchCommand = new RelayCommand(NextSearchMatch, CanNavigateSearchMatch);
        PreviousSearchMatchCommand = new RelayCommand(PreviousSearchMatch, CanNavigateSearchMatch);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => IsSearchActive);
        ShowRenderedCommand = new RelayCommand(
            () => DisplayMode = MarkdownViewerDisplayMode.Rendered,
            () => IsRawMode);
        ShowRawCommand = new RelayCommand(
            () => DisplayMode = MarkdownViewerDisplayMode.Raw,
            () => IsRenderedMode);
    }

    public string Title => string.IsNullOrWhiteSpace(_fileName)
        ? "SharpTurns Markdown Viewer"
        : $"SharpTurns Markdown Viewer · {_fileName}";

    public string FileName => _fileName;

    public string FilePath => _filePath;

    public string Markdown => _markdown;

    public MarkdownViewerDisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsRenderedMode));
                OnPropertyChanged(nameof(IsRawMode));
                ShowRenderedCommand.NotifyCanExecuteChanged();
                ShowRawCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRenderedMode => DisplayMode == MarkdownViewerDisplayMode.Rendered;

    public bool IsRawMode => DisplayMode == MarkdownViewerDisplayMode.Raw;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _searchQuery, value))
            {
                CurrentMatchIndex = -1;
                OnPropertyChanged(nameof(IsSearchActive));
                OnPropertyChanged(nameof(SearchMatchStatusLabel));
                ClearSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int CurrentMatchIndex
    {
        get => _currentMatchIndex;
        private set
        {
            if (SetProperty(ref _currentMatchIndex, value))
            {
                OnPropertyChanged(nameof(SearchMatchStatusLabel));
            }
        }
    }

    public int TotalSearchMatches
    {
        get => _totalSearchMatches;
        private set
        {
            if (SetProperty(ref _totalSearchMatches, value))
            {
                OnPropertyChanged(nameof(HasSearchMatches));
                OnPropertyChanged(nameof(SearchMatchStatusLabel));
                NextSearchMatchCommand.NotifyCanExecuteChanged();
                PreviousSearchMatchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool HasSearchMatches => TotalSearchMatches > 0;

    public string SearchMatchStatusLabel
    {
        get
        {
            if (!IsSearchActive)
            {
                return string.Empty;
            }

            if (TotalSearchMatches == 0)
            {
                return "0 matches";
            }

            return CurrentMatchIndex >= 0
                ? $"{CurrentMatchIndex + 1} of {TotalSearchMatches}"
                : $"{TotalSearchMatches} match" + (TotalSearchMatches == 1 ? string.Empty : "es");
        }
    }

    public string? PickerDirectory => _pickerDirectory;

    public string WorkspaceLabel => string.IsNullOrWhiteSpace(_pickerDirectory)
        ? "No initial directory configured"
        : _pickerDirectory;

    public string StatusMessage => _statusMessage;

    public bool CanRefresh => !string.IsNullOrWhiteSpace(_filePath);

    public Uri? RemoteDocumentUri { get; private set; }

    public bool IsRemoteDocument => RemoteDocumentUri is not null;

    public MarkdownViewerAppearanceViewModel Appearance { get; }

    public MarkdownViewerFileBrowserViewModel FileBrowser { get; }

    public RelayCommand NextSearchMatchCommand { get; }

    public RelayCommand PreviousSearchMatchCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand ShowRenderedCommand { get; }

    public RelayCommand ShowRawCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public void SetDocument(
        string fileName,
        string filePath,
        string markdown,
        DateTimeOffset? modifiedAt = null,
        ulong? byteSize = null,
        Uri? remoteUri = null)
    {
        var document = new DocumentSnapshot(fileName, filePath, markdown, modifiedAt, byteSize, remoteUri);
        if (_historyIndex >= 0 && IsSameDocument(_history[_historyIndex], document))
        {
            // Refreshing/reopening the current document must not discard forward history.
            _history[_historyIndex] = document;
        }
        else
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(document);
            _historyIndex = _history.Count - 1;
        }

        ApplyDocument(document);
    }

    private void NavigateHistory(int direction)
    {
        var index = _historyIndex + direction;
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        _historyIndex = index;
        ApplyDocument(_history[index]);
    }

    private static bool IsSameDocument(DocumentSnapshot left, DocumentSnapshot right) =>
        left.RemoteUri is not null || right.RemoteUri is not null
            ? left.RemoteUri is not null && right.RemoteUri is not null && left.RemoteUri == right.RemoteUri
            : string.Equals(left.FilePath, right.FilePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ApplyDocument(DocumentSnapshot document)
    {
        var (fileName, filePath, markdown, modifiedAt, byteSize, remoteUri) = document;
        var documentChanged = _filePath != filePath || RemoteDocumentUri != remoteUri;
        CancelRemoteLoad();
        RemoteDocumentUri = remoteUri;
        _fileName = fileName;
        _filePath = filePath;
        _markdown = markdown;
        if (remoteUri is null)
        {
            _pickerDirectory = NormalizeDirectory(Path.GetDirectoryName(filePath)) ?? _pickerDirectory;
        }
        var displayPath = string.IsNullOrWhiteSpace(filePath) ? fileName : filePath;
        var modifiedLabel = modifiedAt.HasValue
            ? $" · Modified {FormatFileTimestamp(modifiedAt.Value)}"
            : string.Empty;
        var byteSizeLabel = byteSize.HasValue
            ? $" · {byteSize.Value:N0} bytes"
            : string.Empty;
        _statusMessage = $"{displayPath}{modifiedLabel}{byteSizeLabel}";
        if (documentChanged)
        {
            CurrentMatchIndex = -1;
            SetSearchTotalMatches(0);
            OnPropertyChanged(nameof(FilePath));
        }
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Markdown));
        OnPropertyChanged(nameof(PickerDirectory));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RemoteDocumentUri));
        OnPropertyChanged(nameof(IsRemoteDocument));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    internal Uri? ResolveMarkdownLink(Uri? href)
    {
        if (href is null || string.IsNullOrWhiteSpace(href.OriginalString) ||
            href.OriginalString.StartsWith('#'))
        {
            return null;
        }

        try
        {
            var target = href;
            if (!target.IsAbsoluteUri)
            {
                var baseUri = RemoteDocumentUri;
                if (baseUri is null && !string.IsNullOrWhiteSpace(FilePath))
                {
                    baseUri = new UriBuilder(Uri.UriSchemeFile, string.Empty)
                    {
                        Path = Path.GetFullPath(FilePath),
                    }.Uri;
                }

                if (baseUri is null || !Uri.TryCreate(baseUri, href, out target))
                {
                    return null;
                }
            }

            if (MarkdownDocumentLink.IsRemoteMarkdown(target))
            {
                return target;
            }

            // Remote documents must never navigate into the local filesystem or network shares.
            return !IsRemoteDocument && target.IsFile && !target.IsUnc &&
                   (string.IsNullOrEmpty(target.Host) || target.Host == "localhost") &&
                   MarkdownViewerFileBrowserViewModel.IsMarkdownPath(target.LocalPath)
                ? target
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    internal int BeginLocalDocumentLoad()
    {
        CancelRemoteLoad();
        return _documentLoadVersion;
    }

    internal bool IsDocumentLoadCurrent(int version) => version == _documentLoadVersion;

    internal async Task LoadRemoteDocumentAsync(Uri uri)
    {
        CancelRemoteLoad();
        using var cancellation = new CancellationTokenSource();
        _remoteLoadCancellation = cancellation;
        SetStatus("Downloading Markdown document…");
        try
        {
            var document = await _remoteLoader.LoadAsync(uri, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _remoteLoadCancellation = null;
            SetDocument(Uri.UnescapeDataString(uri.Segments[^1]), uri.AbsoluteUri,
                document.Markdown, document.ModifiedAt, document.ByteSize, uri);
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                SetStatus("Unable to download the Markdown document: the request timed out.");
            }
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                SetStatus($"Unable to download the Markdown document: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_remoteLoadCancellation, cancellation))
            {
                _remoteLoadCancellation = null;
            }
        }
    }

    internal void CancelRemoteLoad()
    {
        // Also invalidate local storage reads, which cannot themselves be canceled.
        _documentLoadVersion++;
        _remoteLoadCancellation?.Cancel();
        _remoteLoadCancellation = null;
    }

    internal static string FormatFileTimestamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public void SetStatus(string statusMessage)
    {
        _statusMessage = statusMessage;
        OnPropertyChanged(nameof(StatusMessage));
    }

    public void SetSearchTotalMatches(int count)
    {
        TotalSearchMatches = Math.Max(0, count);
        if (CurrentMatchIndex >= TotalSearchMatches)
        {
            CurrentMatchIndex = TotalSearchMatches > 0 ? TotalSearchMatches - 1 : -1;
        }
    }

    private void NextSearchMatch()
    {
        if (TotalSearchMatches <= 0)
        {
            return;
        }

        CurrentMatchIndex = (CurrentMatchIndex + 1) % TotalSearchMatches;
    }

    private void PreviousSearchMatch()
    {
        if (TotalSearchMatches <= 0)
        {
            return;
        }

        CurrentMatchIndex = CurrentMatchIndex <= 0
            ? TotalSearchMatches - 1
            : CurrentMatchIndex - 1;
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SetSearchTotalMatches(0);
    }

    private bool CanNavigateSearchMatch() => HasSearchMatches;

    private static string? NormalizeDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
}
