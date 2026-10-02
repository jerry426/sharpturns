using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SharpTurns.MarkdownViewer.App.ViewModels;

public sealed class MarkdownViewerFileBrowserViewModel : ObservableObject
{
    private static readonly StringComparer FileNameComparer = StringComparer.OrdinalIgnoreCase;

    private string? _rootDirectory;
    private MarkdownViewerFileBrowserNode? _selectedNode;
    private string _statusMessage = "Open a Markdown file to browse its directory.";

    public ObservableCollection<MarkdownViewerFileBrowserNode> RootNodes { get; } = [];

    public string? RootDirectory
    {
        get => _rootDirectory;
        private set => SetProperty(ref _rootDirectory, value);
    }

    public MarkdownViewerFileBrowserNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(CanReanchor));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasRoot => RootNodes.Count > 0;

    public bool HasNoRoot => !HasRoot;

    public bool CanReanchor => SelectedNode?.IsDirectory == true;

    public async Task RefreshAsync(string? directory, CancellationToken cancellationToken = default)
    {
        var normalizedDirectory = string.IsNullOrWhiteSpace(directory)
            ? GetUserRootDirectory()
            : NormalizeDirectory(directory);
        if (normalizedDirectory is null)
        {
            RootNodes.Clear();
            RootDirectory = null;
            SelectedNode = null;
            StatusMessage = "Open a Markdown file or workspace folder to browse Markdown files.";
            NotifyRootStateChanged();
            return;
        }

        var root = CreateDirectoryNode(normalizedDirectory);
        root.IsExpanded = true;

        RootNodes.Clear();
        RootNodes.Add(root);
        RootDirectory = normalizedDirectory;
        SelectedNode = null;
        StatusMessage = string.Empty;
        NotifyRootStateChanged();

        await LoadChildrenAsync(root, cancellationToken);
    }

    public async Task<bool> ReanchorAtSelectedDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var selectedDirectory = SelectedNode is { IsDirectory: true } selectedNode
            ? NormalizeDirectory(selectedNode.FullPath)
            : null;
        if (selectedDirectory is null)
        {
            return false;
        }

        await RefreshAsync(selectedDirectory, cancellationToken);
        return true;
    }

    public async Task LoadChildrenAsync(
        MarkdownViewerFileBrowserNode node,
        CancellationToken cancellationToken = default)
    {
        if (!node.IsDirectory || node.ChildrenLoaded || node.IsLoadingChildren)
        {
            return;
        }

        node.IsLoadingChildren = true;
        try
        {
            var result = await Task.Run(
                () => ReadChildren(node.FullPath),
                cancellationToken);

            node.Children.Clear();
            foreach (var child in result.Children)
            {
                node.Children.Add(child);
            }

            if (result.ErrorMessage is not null)
            {
                node.Children.Add(new MarkdownViewerFileBrowserNode(
                    result.ErrorMessage,
                    string.Empty,
                    MarkdownViewerFileBrowserNodeKind.Message));
            }

            node.ChildrenLoaded = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new MarkdownViewerFileBrowserNode(
                $"Unable to read folder: {ex.Message}",
                string.Empty,
                MarkdownViewerFileBrowserNodeKind.Message));
            node.ChildrenLoaded = true;
        }
        finally
        {
            node.IsLoadingChildren = false;
        }
    }

    public async Task EnsureFileVisibleAsync(
        string? filePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedFilePath = NormalizeFilePath(filePath);
        if (normalizedFilePath is null)
        {
            return;
        }

        var root = RootNodes.FirstOrDefault();
        if (root is null || !IsPathWithinDirectory(root.FullPath, normalizedFilePath))
        {
            var containingDirectory = Path.GetDirectoryName(normalizedFilePath);
            await RefreshAsync(containingDirectory, cancellationToken);
            root = RootNodes.FirstOrDefault();
        }

        if (root is null || !IsPathWithinDirectory(root.FullPath, normalizedFilePath))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(root.FullPath, normalizedFilePath);
        var pathSegments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length == 0)
        {
            return;
        }

        var currentNode = root;
        for (var index = 0; index < pathSegments.Length; index++)
        {
            await LoadChildrenAsync(currentNode, cancellationToken);

            var child = currentNode.Children.FirstOrDefault(candidate =>
                !candidate.IsMessage &&
                !candidate.IsLoading &&
                FileNameComparer.Equals(candidate.Name, pathSegments[index]));
            if (child is null)
            {
                return;
            }

            currentNode = child;
            if (index < pathSegments.Length - 1)
            {
                if (!currentNode.IsDirectory)
                {
                    return;
                }

                currentNode.IsExpanded = true;
            }
        }

        if (currentNode.IsMarkdownFile)
        {
            SelectedNode = currentNode;
        }
    }

    internal static bool IsMarkdownPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPathWithinDirectory(string directory, string path)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return relativePath == "." ||
               (!Path.IsPathRooted(relativePath) &&
                relativePath != ".." &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static FileBrowserReadResult ReadChildren(string directory)
    {
        var children = new List<MarkdownViewerFileBrowserNode>();
        string? errorMessage = null;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (!IsReparsePoint(entry))
                        {
                            children.Add(CreateDirectoryNode(entry));
                        }

                        continue;
                    }

                    if (File.Exists(entry) && IsMarkdownPath(entry))
                    {
                        children.Add(new MarkdownViewerFileBrowserNode(
                            GetDisplayName(entry),
                            entry,
                            MarkdownViewerFileBrowserNodeKind.MarkdownFile));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errorMessage ??= $"Unable to inspect an item: {ex.Message}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Unable to read folder: {ex.Message}";
        }

        var orderedChildren = children
            .OrderBy(child => child.IsParentDirectory ? 0 : child.IsDirectory ? 1 : 2)
            .ThenBy(child => child.Name, FileNameComparer)
            .ToList();
        var parentDirectory = GetNavigationParent(directory);
        if (parentDirectory is not null)
        {
            orderedChildren.Insert(
                0,
                new MarkdownViewerFileBrowserNode(
                    "..",
                    parentDirectory,
                    MarkdownViewerFileBrowserNodeKind.ParentDirectory));
        }

        return new FileBrowserReadResult(orderedChildren, errorMessage);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static MarkdownViewerFileBrowserNode CreateDirectoryNode(string path)
    {
        var directory = new MarkdownViewerFileBrowserNode(
            GetDisplayName(path),
            path,
            MarkdownViewerFileBrowserNodeKind.Directory);
        directory.Children.Add(CreateLoadingNode());
        return directory;
    }

    private static MarkdownViewerFileBrowserNode CreateLoadingNode() =>
        new("Loading…", string.Empty, MarkdownViewerFileBrowserNodeKind.Loading);

    private static string? GetUserRootDirectory()
    {
        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return NormalizeDirectory(userRoot);
    }

    private static string? GetNavigationParent(string directory)
    {
        try
        {
            var userRoot = GetUserRootDirectory();
            if (userRoot is null ||
                !IsPathWithinDirectory(userRoot, directory) ||
                string.Equals(
                    Path.GetRelativePath(userRoot, directory),
                    ".",
                    StringComparison.Ordinal))
            {
                return null;
            }

            var parent = Directory.GetParent(directory)?.FullName;
            return parent is not null && IsPathWithinDirectory(userRoot, parent)
                ? parent
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(directory);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string GetDisplayName(string path)
    {
        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(pathRoot) &&
            string.Equals(
                trimmedPath,
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return pathRoot;
        }

        var fileName = Path.GetFileName(trimmedPath);
        return string.IsNullOrEmpty(fileName) ? path : fileName;
    }

    private void NotifyRootStateChanged()
    {
        OnPropertyChanged(nameof(HasRoot));
        OnPropertyChanged(nameof(HasNoRoot));
    }

    private sealed record FileBrowserReadResult(
        IReadOnlyList<MarkdownViewerFileBrowserNode> Children,
        string? ErrorMessage);
}
