using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;
using SharpTurns.Core.InstanceManagement;

namespace SharpTurns.InstanceManager.App.ViewModels;

/// <summary>
/// One card in the Instance Manager representing a single running SharpTurns instance. Updated in place when the
/// reported state changes.
/// </summary>
public sealed partial class InstanceCardViewModel : ObservableObject
{
    public string Id { get; }
    public int Pid { get; private set; }
    public long? ProjectId { get; private set; }
    public long? ConversationId { get; private set; }

    [ObservableProperty] private string? _projectTitle;
    [ObservableProperty] private string _projectBorderColor = ProjectColor.Default;
    [ObservableProperty] private string? _conversationTitle;
    [ObservableProperty] private string? _conversationModel;
    [ObservableProperty] private string _pidLabel = "";
    [ObservableProperty] private string _heartbeatLabel = "";
    [ObservableProperty] private string _cpuLabel = "CPU —";
    [ObservableProperty] private bool _turnActive;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLinked;
    [ObservableProperty] private bool _isProcessActionPending;
    /// <summary>When true, the card shows only its title, model, CPU, and turn-in-progress rows.</summary>
    [ObservableProperty] private bool _isCollapsed;

    public RelayCommand StopCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand FocusCommand { get; }
    /// <summary>Attaches the instance when detached and detaches it when linked.</summary>
    public RelayCommand ToggleAttachCommand { get; }
    public RelayCommand ToggleCollapsedCommand { get; }

    public string CollapseToggleLabel => IsCollapsed ? "▸" : "▾";
    public string CollapseToggleToolTip => IsCollapsed ? "Expand this card" : "Collapse this card";

    public string AttachToggleLabel => IsLinked ? "Detach" : "Attach";
    public string AttachToggleToolTip => IsLinked
        ? "Return this SharpTurns instance to standalone mode so it can be moved independently."
        : "Attach this SharpTurns instance to Instance Manager without restarting it.";

    partial void OnTurnActiveChanged(bool value) => NotifyProcessCommands();
    partial void OnIsProcessActionPendingChanged(bool value)
    {
        NotifyProcessCommands();
        ToggleAttachCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsLinkedChanged(bool value)
    {
        OnPropertyChanged(nameof(AttachToggleLabel));
        OnPropertyChanged(nameof(AttachToggleToolTip));
        ToggleAttachCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(CollapseToggleLabel));
        OnPropertyChanged(nameof(CollapseToggleToolTip));
    }

    private void NotifyProcessCommands()
    {
        StopCommand?.NotifyCanExecuteChanged();
        ReloadCommand?.NotifyCanExecuteChanged();
    }

    public bool HasConversationModel => !string.IsNullOrWhiteSpace(ConversationModel);

    partial void OnConversationModelChanged(string? value) => OnPropertyChanged(nameof(HasConversationModel));

    public InstanceCardViewModel(
        AppInstanceRecord record,
        Action<InstanceCardViewModel>? stopAction = null,
        Action<InstanceCardViewModel>? focusAction = null,
        Action<InstanceCardViewModel>? reloadAction = null,
        Action<InstanceCardViewModel>? detachAction = null,
        Action<InstanceCardViewModel>? attachAction = null)
    {
        Id = record.Id;
        StopCommand = new RelayCommand(() => stopAction?.Invoke(this), () => stopAction is not null && !TurnActive && !IsProcessActionPending);
        ReloadCommand = new RelayCommand(() => reloadAction?.Invoke(this), () => reloadAction is not null && !TurnActive && !IsProcessActionPending);
        FocusCommand = new RelayCommand(() => focusAction?.Invoke(this), () => focusAction is not null);
        ToggleAttachCommand = new RelayCommand(
            () => (IsLinked ? detachAction : attachAction)?.Invoke(this),
            () => (IsLinked ? detachAction : attachAction) is not null && !IsProcessActionPending);
        ToggleCollapsedCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
        UpdateFrom(record);
    }

    public void UpdateFrom(AppInstanceRecord record)
    {
        Pid = record.Pid;
        ProjectId = record.ProjectId;
        ConversationId = record.ConversationId;
        ProjectTitle = record.ProjectTitle;
        ProjectBorderColor = ProjectColor.Normalize(record.ProjectColor);
        ConversationTitle = record.ConversationTitle;
        ConversationModel = record.ConversationModel;
        TurnActive = record.TurnActive;
        PidLabel = $"PID {record.Pid}";
        // Liveness-only reports do not invalidate cards; the server removes the card when its receipt deadline expires.
        HeartbeatLabel = "♥ Local";
    }
}
