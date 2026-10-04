using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>A context file role as the Edit Conversation dialog offers it; ShortName labels it on the sidebar card.</summary>
public sealed record ContextFileRoleOption(string Value, string DisplayName, string ShortName, string Description)
{
    public static IReadOnlyList<ContextFileRoleOption> All { get; } =
    [
        new(ContextFileRoles.NormativeGuidance, "Normative guidance", "Guidance", "Rules the model should apply when relevant."),
        new(ContextFileRoles.ActiveOperationalDocument, "Active operational document", "Active roadmap",
            "The current roadmap or plan that guides ongoing work."),
        new(ContextFileRoles.ReferenceSource, "Reference source", "Reference", "Source context that is not governing instruction by default."),
        new(ContextFileRoles.HistoricalEvidence, "Historical evidence", "Historical", "A past decision, event, result, or state."),
    ];

    public static ContextFileRoleOption For(string role) => All.FirstOrDefault(o => o.Value == role) ?? All[2];
}

/// <summary>One file's row in the Edit Conversation dialog. changed runs after any setting changes.</summary>
public sealed partial class ContextFileEditViewModel : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleDescription))]
    private ContextFileRoleOption _selectedRole;

    [ObservableProperty]
    private string _purpose;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private bool _allowModelMaintenance;

    [ObservableProperty]
    private string _availabilityLabel = "Not checked";

    [ObservableProperty]
    private bool _isAvailable;

    public ContextFileEditViewModel(ContextFile file, Action<ContextFileEditViewModel> remove,
        Action<ContextFileEditViewModel, int> move, Func<ContextFileEditViewModel, int, bool> canMove, Action changed)
    {
        Path = file.Path;
        _selectedRole = ContextFileRoleOption.For(file.Role);
        _purpose = file.Purpose ?? "";
        _enabled = file.Enabled;
        _required = file.Required;
        _allowModelMaintenance = file.AllowModelMaintenance;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
        MoveUpCommand = new RelayCommand(() => move(this, -1), () => canMove(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1), () => canMove(this, 1));
    }

    /// <summary>Relative to the conversation's workspace.</summary>
    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public IReadOnlyList<ContextFileRoleOption> RoleOptions => ContextFileRoleOption.All;

    public string RoleDescription => SelectedRole.Description;

    public IRelayCommand RemoveCommand { get; }

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    partial void OnEnabledChanged(bool value) => _changed();

    /// <summary>Checks the file under workspace, as a turn would find it; only its presence and size.</summary>
    public void RefreshAvailability(string? workspace)
    {
        if (workspace is null || !Directory.Exists(workspace))
        {
            (IsAvailable, AvailabilityLabel) = (false, "Workspace unavailable");
            return;
        }
        try
        {
            var info = new FileInfo(System.IO.Path.Combine(workspace, Path));
            (IsAvailable, AvailabilityLabel) = !info.Exists ? (false, "Missing")
                : info.Length == 0 ? (false, "Empty")
                : (true, string.Create(CultureInfo.CurrentCulture, $"Available · {info.Length:N0} bytes"));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            (IsAvailable, AvailabilityLabel) = (false, "Unavailable");
        }
    }

    public void NotifyPositionChanged()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    public ContextFile ToContextFile() => new(Path, SelectedRole.Value,
        string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(), Enabled, Required, AllowModelMaintenance);
}

/// <summary>An enabled file on the sidebar's Conversation Configuration card.</summary>
public sealed record ContextFileSummaryViewModel(string FileName, string Path, string DetailLabel)
{
    public static ContextFileSummaryViewModel From(ContextFile file)
    {
        var attributes = new List<string> { ContextFileRoleOption.For(file.Role).ShortName };
        if (file.Required) attributes.Add("Required");
        if (file.AllowModelMaintenance) attributes.Add("Model may maintain");
        return new(file.FileName, file.Path, string.Join(" · ", attributes));
    }
}
