using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.InstanceManager.App.ViewModels;

/// <summary>
/// One project card holding that project's instance cards in user-arranged order. Instances without a project share a
/// group with a null <see cref="ProjectId"/>.
/// </summary>
public sealed partial class ProjectGroupViewModel : ObservableObject
{
    public long? ProjectId { get; }

    [ObservableProperty] private string? _projectTitle;
    [ObservableProperty] private string _projectBorderColor = ProjectColor.Default;
    /// <summary>True while one of this group's instance cards is the selected one.</summary>
    [ObservableProperty] private bool _hasSelectedInstance;

    public ObservableCollection<InstanceCardViewModel> Instances { get; } = new();

    public ProjectGroupViewModel(long? projectId) => ProjectId = projectId;

    /// <summary>Takes the project's display title and color from a member instance.</summary>
    public void UpdateFrom(InstanceCardViewModel card)
    {
        ProjectTitle = card.ProjectTitle;
        ProjectBorderColor = card.ProjectBorderColor;
    }
}
