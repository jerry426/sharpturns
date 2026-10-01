using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private Color _selectedColor;

    public ProjectDialogViewModel(string title, Project? project = null)
    {
        Title = title;
        _name = project?.Name ?? "";
        _workingDirectory = project?.WorkingDirectory ?? "";
        _selectedColor = Color.Parse(ProjectColor.Normalize(project?.Color));
    }

    public string Title { get; }

    /// <summary>Project colors are opaque, so the picker's alpha is dropped.</summary>
    public Color SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (SetProperty(ref _selectedColor, Color.FromRgb(value.R, value.G, value.B)))
                OnPropertyChanged(nameof(ColorHex));
        }
    }

    public string ColorHex => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";

    public string? DirectoryError =>
        string.IsNullOrWhiteSpace(WorkingDirectory) || Directory.Exists(WorkingDirectory.Trim()) ? null : "This folder doesn't exist.";

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory.Trim());

    [RelayCommand]
    private void ResetColor() => SelectedColor = Color.Parse(ProjectColor.Default);
}
