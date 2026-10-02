using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SharpTurns.MarkdownViewer.App.ViewModels;

public enum MarkdownViewerFileBrowserNodeKind
{
    ParentDirectory,
    Directory,
    MarkdownFile,
    Loading,
    Message,
}

public sealed class MarkdownViewerFileBrowserNode : ObservableObject
{
    private bool _isExpanded;

    internal MarkdownViewerFileBrowserNode(
        string name,
        string fullPath,
        MarkdownViewerFileBrowserNodeKind kind)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
    }

    public string Name { get; }

    public string FullPath { get; }

    public MarkdownViewerFileBrowserNodeKind Kind { get; }

    public bool IsDirectory => Kind == MarkdownViewerFileBrowserNodeKind.Directory;

    public bool IsParentDirectory => Kind == MarkdownViewerFileBrowserNodeKind.ParentDirectory;

    public bool IsMarkdownFile => Kind == MarkdownViewerFileBrowserNodeKind.MarkdownFile;

    public bool IsLoading => Kind == MarkdownViewerFileBrowserNodeKind.Loading;

    public bool IsMessage => Kind == MarkdownViewerFileBrowserNodeKind.Message;

    public bool IsSelectable => IsMarkdownFile || IsParentDirectory;

    public string Icon => Kind switch
    {
        MarkdownViewerFileBrowserNodeKind.ParentDirectory => "↥",
        MarkdownViewerFileBrowserNodeKind.Directory => "📁",
        MarkdownViewerFileBrowserNodeKind.MarkdownFile => "M↓",
        MarkdownViewerFileBrowserNodeKind.Loading => "…",
        _ => "!",
    };

    public ObservableCollection<MarkdownViewerFileBrowserNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    internal bool ChildrenLoaded { get; set; }

    internal bool IsLoadingChildren { get; set; }
}
