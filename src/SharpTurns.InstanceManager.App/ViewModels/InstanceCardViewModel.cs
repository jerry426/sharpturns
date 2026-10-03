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

    public RelayCommand StopCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand FocusCommand { get; }

    partial void OnTurnActiveChanged(bool value) => NotifyProcessCommands();
    partial void OnIsProcessActionPendingChanged(bool value) => NotifyProcessCommands();

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
        Action<InstanceCardViewModel>? reloadAction = null)
    {
        Id = record.Id;
        StopCommand = new RelayCommand(() => stopAction?.Invoke(this), () => stopAction is not null && !TurnActive && !IsProcessActionPending);
        ReloadCommand = new RelayCommand(() => reloadAction?.Invoke(this), () => reloadAction is not null && !TurnActive && !IsProcessActionPending);
        FocusCommand = new RelayCommand(() => focusAction?.Invoke(this), () => focusAction is not null);
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
