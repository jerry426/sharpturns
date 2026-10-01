using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

public sealed partial class ProjectDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(DirectoryError))]
    private string _workingDirectory;

    public ProjectDialogViewModel(string title, Project? project = null)
    {
        Title = title;
        _name = project?.Name ?? "";
        _workingDirectory = project?.WorkingDirectory ?? "";
    }

    public string Title { get; }

    public string? DirectoryError =>
        string.IsNullOrWhiteSpace(WorkingDirectory) || Directory.Exists(WorkingDirectory.Trim()) ? null : "This folder doesn't exist.";

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory.Trim());
}
