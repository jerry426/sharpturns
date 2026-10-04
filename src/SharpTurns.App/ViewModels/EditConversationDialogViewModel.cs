using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Edit Conversation dialog: the title, the project (a different one moves the conversation), an optional
/// workspace in place of the project's, protection from deletion, a summarizer model and effort in place of the
/// default's, the MCP servers its turns start, and the context files they share with Claude.
/// </summary>
public sealed partial class EditConversationDialogViewModel : ObservableObject
{
    private readonly Project _originalProject;
    private readonly string _defaultSummarizerModelOption;
    private readonly string _defaultSummarizerEffortOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextFilesError))]
    private string? _contextFilesError;

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

    [ObservableProperty]
    private string _selectedSummarizerModel;

    [ObservableProperty]
    private string _selectedSummarizerEffort;

    /// <summary>
    /// selectedServerIds are the conversation's saved MCP servers; projects includes its own. models is the Config tab's
    /// list, and defaultSummarizerModel and defaultSummarizerEffort are Config → Preferences' choices.
    /// </summary>
    public EditConversationDialogViewModel(Conversation conversation, IReadOnlyList<Project> projects,
        IReadOnlyList<McpServer> servers, IReadOnlyCollection<long> selectedServerIds, IReadOnlyList<ContextFile> contextFiles,
        IReadOnlyList<string> models, string defaultSummarizerModel, string defaultSummarizerEffort)
    {
        foreach (var file in contextFiles) ContextFiles.Add(CreateContextFile(file));
        Projects = projects;
        _originalProject = projects.First(p => p.Id == conversation.ProjectId);
        _title = conversation.Title;
        _selectedProject = _originalProject;
        _workingDirectory = conversation.WorkingDirectory ?? "";
        _isProtected = conversation.IsProtected;
        _defaultSummarizerModelOption = $"Default ({defaultSummarizerModel})";
        _defaultSummarizerEffortOption = defaultSummarizerEffort == ConversationViewModel.DefaultEffort
            ? ConversationViewModel.DefaultEffort : $"Default ({defaultSummarizerEffort})";
        // A saved model since removed from the list stays offered, so saving doesn't drop it.
        SummarizerModelOptions = conversation.SummarizerModel is { } saved && !models.Contains(saved)
            ? [_defaultSummarizerModelOption, .. models, saved]
            : [_defaultSummarizerModelOption, .. models];
        SummarizerEffortOptions = [_defaultSummarizerEffortOption, .. ConversationViewModel.SharedEffortOptions.Skip(1)];
        _selectedSummarizerModel = conversation.SummarizerModel ?? _defaultSummarizerModelOption;
        _selectedSummarizerEffort = conversation.SummarizerEffort is { } effort && SummarizerEffortOptions.Contains(effort)
            ? effort : _defaultSummarizerEffortOption;
        McpServers = servers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new McpServerOptionViewModel(s, selectedServerIds.Contains(s.Id), NotifyMcpServersChanged))
            .ToArray();
        NotifyContextFilesChanged();
        RefreshContextFiles();
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

    /// <summary>The default summarizer model, then the Config tab's models.</summary>
    public IReadOnlyList<string> SummarizerModelOptions { get; }

    public IReadOnlyList<string> SummarizerEffortOptions { get; }

    /// <summary>Null uses the default summarizer model.</summary>
    public string? SummarizerModelOrNull =>
        SelectedSummarizerModel is { } model && model != _defaultSummarizerModelOption ? model : null;

    /// <summary>Null uses the default summarizer effort.</summary>
    public string? SummarizerEffortOrNull =>
        SelectedSummarizerEffort is { } effort && effort != _defaultSummarizerEffortOption ? effort : null;

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

    public IReadOnlyList<McpServer> SelectedServers => McpServers.Where(s => s.IsSelected).Select(s => s.Server)
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public ObservableCollection<ContextFileEditViewModel> ContextFiles { get; } = [];

    public bool HasContextFiles => ContextFiles.Count > 0;

    public string ContextFilesHeaderLabel => HasContextFiles
        ? string.Create(CultureInfo.CurrentCulture,
            $"Additional Context Files ({ContextFiles.Count:N0} configured, {ContextFiles.Count(f => f.Enabled):N0} enabled)")
        : "Additional Context Files (none configured)";

    public string ContextFilesRootLabel => ContextFilesRoot is { } root
        ? $"Paths are stored relative to {root}"
        : "Choose an existing workspace before adding files.";

    public bool HasContextFilesError => ContextFilesError is not null;

    public IReadOnlyList<ContextFile> ContextFileSettings => ContextFiles.Select(f => f.ToContextFile()).ToArray();

    // Files are stored relative to the workspace the conversation will have once saved.
    private string? ContextFilesRoot => WorkspaceError is null && SelectedProject is { } project
        && (WorkingDirectoryOrNull ?? project.WorkingDirectory) is var root && Directory.Exists(root)
        ? Path.GetFullPath(root)
        : null;

    /// <summary>Adds a file chosen in the picker; reports why one can't be added in ContextFilesError.</summary>
    public bool TryAddContextFile(string fullPath)
    {
        string? error;
        if (ContextFiles.Count >= Core.ContextFiles.MaxFiles)
            error = $"A conversation can have at most {Core.ContextFiles.MaxFiles} context files.";
        else if (ContextFilesRoot is not { } root) error = "Choose an existing workspace before adding files.";
        else if (!File.Exists(fullPath)) error = "Choose an existing text file.";
        else
        {
            string relative;
            try { relative = Core.ContextFiles.RelativePath(root, fullPath); }
            catch (ArgumentException)
            {
                ContextFilesError = $"{Path.GetFileName(fullPath)} isn't inside the workspace, {root}.";
                return false;
            }
            if (ContextFiles.Any(f => string.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase)))
                error = $"{relative} is already added.";
            else
            {
                var file = CreateContextFile(new(relative, ContextFileRoles.ReferenceSource, null, true, false, false));
                file.RefreshAvailability(root);
                ContextFiles.Add(file);
                ContextFilesError = null;
                NotifyContextFilesChanged();
                return true;
            }
        }
        ContextFilesError = error;
        return false;
    }

    partial void OnSelectedProjectChanged(Project? value) => RefreshContextFiles();

    partial void OnWorkingDirectoryChanged(string value) => RefreshContextFiles();

    private ContextFileEditViewModel CreateContextFile(ContextFile file) =>
        new(file, RemoveContextFile, MoveContextFile, CanMoveContextFile, NotifyContextFilesChanged);

    private void RemoveContextFile(ContextFileEditViewModel file)
    {
        if (!ContextFiles.Remove(file)) return;
        ContextFilesError = null;
        NotifyContextFilesChanged();
    }

    private bool CanMoveContextFile(ContextFileEditViewModel file, int offset) =>
        ContextFiles.IndexOf(file) + offset is var target && target >= 0 && target < ContextFiles.Count;

    private void MoveContextFile(ContextFileEditViewModel file, int offset)
    {
        if (!CanMoveContextFile(file, offset)) return;
        var index = ContextFiles.IndexOf(file);
        ContextFiles.Move(index, index + offset);
        NotifyContextFilesChanged();
    }

    private void NotifyContextFilesChanged()
    {
        foreach (var file in ContextFiles) file.NotifyPositionChanged();
        OnPropertyChanged(nameof(HasContextFiles));
        OnPropertyChanged(nameof(ContextFilesHeaderLabel));
    }

    // The workspace or project changed, so the same relative paths may point elsewhere.
    private void RefreshContextFiles()
    {
        var root = ContextFilesRoot;
        foreach (var file in ContextFiles) file.RefreshAvailability(root);
        OnPropertyChanged(nameof(ContextFilesRootLabel));
    }

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

    public McpServer Server => server;

    public long Id => server.Id;

    public string Label => server.Enabled ? server.DisplayName : $"{server.DisplayName} (disabled; not started)";

    public string ToolTip => server.Description is null ? server.Name : $"{server.Name}: {server.Description}";

    partial void OnIsSelectedChanged(bool value) => selectionChanged();
}
