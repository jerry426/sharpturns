using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SharpTurns.MarkdownViewer.App.Startup;
using SharpTurns.MarkdownViewer.App.ViewModels;
using SharpTurns.MarkdownViewer.App.Views;

namespace SharpTurns.MarkdownViewer.App.Services;

internal sealed class ViewerWindowCoordinator
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly HashSet<MarkdownViewerWindow> _windows = [];
    private readonly Queue<string> _pendingActivatedFiles = [];
    private double? _defaultWindowWidth;
    private double? _defaultWindowHeight;

    public ViewerWindowCoordinator(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
    }

    public void OpenInitialWindow(StartupOptions options)
    {
        _defaultWindowWidth = options.WindowWidth;
        _defaultWindowHeight = options.WindowHeight;
        var window = CreateWindow(options);
        _desktop.MainWindow = window;

        if (_pendingActivatedFiles.Count > 0)
        {
            var pendingFiles = _pendingActivatedFiles.ToArray();
            _pendingActivatedFiles.Clear();
            _ = OpenActivatedFilePathsAsync(pendingFiles);
        }
    }

    public async Task OpenActivatedFilesAsync(IReadOnlyList<IStorageItem> items)
    {
        var filePaths = items
            .OfType<IStorageFile>()
            .Select(file => file.TryGetLocalPath())
            .OfType<string>()
            .Where(IsMarkdownFilePath)
            .Where(File.Exists)
            .Distinct(GetPathComparer())
            .ToArray();

        if (filePaths.Length == 0)
        {
            return;
        }

        if (_windows.Count == 0)
        {
            foreach (var filePath in filePaths)
            {
                _pendingActivatedFiles.Enqueue(filePath);
            }

            return;
        }

        await OpenActivatedFilePathsAsync(filePaths);
    }

    private async Task OpenActivatedFilePathsAsync(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            var existingWindow = _windows.FirstOrDefault(window => window.IsShowingOrOpeningFile(filePath));
            if (existingWindow is not null)
            {
                existingWindow.Activate();
                continue;
            }

            var emptyWindow = _windows.FirstOrDefault(window => window.CanReceiveActivatedFile);
            if (emptyWindow is not null)
            {
                await emptyWindow.LoadActivatedFileAsync(filePath);
                continue;
            }

            OpenAdditionalWindow(Path.GetDirectoryName(filePath), filePath);
        }
    }

    private void OpenAdditionalWindow(string? workspaceDirectory, string? markdownFilePath = null)
    {
        var window = CreateWindow(new StartupOptions(markdownFilePath, workspaceDirectory, null)
        {
            WindowWidth = _defaultWindowWidth,
            WindowHeight = _defaultWindowHeight,
        });
        window.Show();
    }

    private MarkdownViewerWindow CreateWindow(StartupOptions options)
    {
        var viewModel = new MarkdownViewerViewModel(options.WorkspaceDirectory);
        if (!string.IsNullOrWhiteSpace(options.ErrorMessage))
        {
            viewModel.SetStatus(options.ErrorMessage);
        }

        var window = new MarkdownViewerWindow(
            viewModel,
            options.MarkdownFilePath,
            windowWidth: options.WindowWidth,
            windowHeight: options.WindowHeight);

        if (options.ScreenPoint is { } point && window.Screens.ScreenFromPoint(point) is { } screen)
        {
            // CenterScreen uses the pre-show Position to choose the monitor and
            // handles its scaling, working area, and native window decorations.
            window.Position = screen.WorkingArea.Position;
        }

        window.NewWindowRequested += (_, _) => OpenAdditionalWindow(viewModel.PickerDirectory);
        window.Closed += (_, _) => OnWindowClosed(window);
        _windows.Add(window);
        return window;
    }

    private static bool IsMarkdownFilePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void OnWindowClosed(MarkdownViewerWindow window)
    {
        _windows.Remove(window);
        if (_windows.Count == 0)
        {
            _desktop.Shutdown();
        }
    }
}
