using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Edit Conversation dialog: the title, the project (a different one moves the conversation), an optional
/// workspace in place of the project's, protection from deletion, and the MCP servers its turns start.
/// </summary>
public sealed partial class EditConversationDialogViewModel : ObservableObject
{
    private readonly Project _originalProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectChanged), nameof(ProjectChangeLabel), nameof(WorkspacePlaceholder), nameof(CanSave))]
    private Project? _selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceError), nameof(CanSave))]
    private string _workingDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionLabel))]
    private bool _isProtected;

    /// <summary>selectedServerIds are the conversation's saved MCP servers; projects includes its own.</summary>
    public EditConversationDialogViewModel(Conversation conversation, IReadOnlyList<Project> projects,
        IReadOnlyList<McpServer> servers, IReadOnlyCollection<long> selectedServerIds)
    {
        Projects = projects;
        _originalProject = projects.First(p => p.Id == conversation.ProjectId);
        _title = conversation.Title;
        _selectedProject = _originalProject;
        _workingDirectory = conversation.WorkingDirectory ?? "";
        _isProtected = conversation.IsProtected;
        McpServers = servers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new McpServerOptionViewModel(s, selectedServerIds.Contains(s.Id), NotifyMcpServersChanged))
            .ToArray();
    }

    public IReadOnlyList<Project> Projects { get; }

    public bool HasProjectChanged => SelectedProject is { } project && project.Id != _originalProject.Id;

    public string ProjectChangeLabel => HasProjectChanged ? $"Moves from {_originalProject.Name} to {SelectedProject!.Name}" : "";

    /// <summary>A blank workspace uses the selected project's working directory.</summary>
    public string WorkspacePlaceholder => $"The project's: {(SelectedProject ?? _originalProject).WorkingDirectory}";

    public string? WorkspaceError => WorkingDirectory.Trim() switch
    {
        "" => null,
        var path when !Path.IsPathFullyQualified(path) => "Enter a full path.",
        var path when !Directory.Exists(path) => "This folder doesn't exist.",
        _ => null,
    };

    public string ProtectionLabel => IsProtected
        ? "Protected conversations can't be deleted, alone or with their project, until this is cleared."
        : "Unprotected conversations can be deleted from the sidebar.";

    public IReadOnlyList<McpServerOptionViewModel> McpServers { get; }

    public bool HasMcpServers => McpServers.Count > 0;

    public string McpServersHeaderLabel => HasMcpServers
        ? string.Create(CultureInfo.CurrentCulture,
            $"MCP Servers ({McpServers.Count:N0} configured, {McpServers.Count(s => s.IsSelected):N0} selected)")
        : "MCP Servers (none configured)";

    public bool CanSave => !string.IsNullOrWhiteSpace(Title) && SelectedProject is not null && WorkspaceError is null;

    /// <summary>The trimmed workspace, or null to use the project's.</summary>
    public string? WorkingDirectoryOrNull => string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim();

    public IReadOnlyList<long> SelectedServerIds => McpServers.Where(s => s.IsSelected).Select(s => s.Id).ToArray();

    private void NotifyMcpServersChanged() => OnPropertyChanged(nameof(McpServersHeaderLabel));
}

/// <summary>
/// A server's checkbox. A disabled server stays selectable, so disabling it for a while keeps the selection, but
/// isn't started.
/// </summary>
public sealed partial class McpServerOptionViewModel(McpServer server, bool isSelected, Action selectionChanged) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    public long Id => server.Id;

    public string Label => server.Enabled ? server.DisplayName : $"{server.DisplayName} (disabled; not started)";

    public string ToolTip => server.Description is null ? server.Name : $"{server.Name}: {server.Description}";

    partial void OnIsSelectedChanged(bool value) => selectionChanged();
}
