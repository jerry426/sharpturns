using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.InstanceManager.App.Services;

namespace SharpTurns.InstanceManager.App.ViewModels;

/// <summary>
/// Shows live local IPC reports as instance cards grouped into project cards and coordinates window linking.
/// </summary>
public sealed partial class InstanceManagerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly InstanceManagerServer _server;
    private readonly InstanceLauncher _launcher;
    private readonly InstanceFocuser _focuser;
    private readonly LinkedWindowCoordinator _windowCoordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (int Pid, ProcessCpuSampler Sampler)> _cpuSamplers = new();
    private Task? _cpuSamplingTask;
    private int _selectionGeneration;
    private string? _pendingAutoAttachInstanceId;

    private bool _disposed;

    // One project card per project; order is in-memory only.
    public ObservableCollection<ProjectGroupViewModel> Projects { get; } = new();

    // Actions handed to each card so its buttons, and a click on the card, call back into the manager without needing a
    // relative-source DataContext binding.
    private readonly Action<InstanceCardViewModel> _stopAction;
    private readonly Action<InstanceCardViewModel> _reloadAction;
    // Clicking a card or its Front button brings the instance to the front without linking it; only the Attach button
    // and launch auto-attach link an instance.
    private readonly Action<InstanceCardViewModel> _focusAction;
    private readonly Action<InstanceCardViewModel> _attachAction;
    private readonly Action<InstanceCardViewModel> _detachAction;

    // Optional confirmation gates (set by App.axaml.cs) that prompt the user before ending or reloading a process. When
    // null (e.g. in tests) the instance is ended without prompting.
    private readonly Func<InstanceCardViewModel, Task<bool>>? _confirmEndProcess;
    private readonly Func<InstanceCardViewModel, Task<bool>>? _confirmReload;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchProjectCommand))]
    private bool _canLaunch;
    [ObservableProperty] private InstanceCardViewModel? _selectedInstance;
    [ObservableProperty] private bool _hasLinkedInstance;
    // New cards follow the last Collapse All or Expand All.
    [ObservableProperty] private bool _isCollapsed = true;

    public InstanceManagerViewModel(
        InstanceManagerServer server,
        InstanceLauncher launcher,
        InstanceFocuser focuser,
        LinkedWindowCoordinator windowCoordinator,
        Func<InstanceCardViewModel, Task<bool>>? confirmEndProcess = null,
        Func<InstanceCardViewModel, Task<bool>>? confirmReload = null)
    {
        _server = server;
        _server.InstanceChanged += OnInstanceChanged;
        _launcher = launcher;
        _focuser = focuser;
        _windowCoordinator = windowCoordinator;
        _windowCoordinator.ConnectionChanged += OnLinkedWindowConnectionChanged;
        _confirmEndProcess = confirmEndProcess;
        _confirmReload = confirmReload;
        _stopAction = card => _ = ConfirmAndStopAsync(card);
        _reloadAction = card => _ = ConfirmAndStopAsync(card, reload: true);
        _focusAction = card => _ = FocusInstanceAsync(card);
        _attachAction = card => _ = AttachInstanceAsync(card);
        _detachAction = card => _ = DetachInstanceAsync(card);
        CanLaunch = launcher.HasLaunchTarget;
    }

    private async Task FocusInstanceAsync(InstanceCardViewModel card)
    {
        if (card.IsLinked)
        {
            // Already linked: AttachInstanceAsync only selects and activates it.
            await AttachInstanceAsync(card).ConfigureAwait(true);
            return;
        }

        // Raise the standalone window wherever it currently is.
        var focused = await _focuser.FocusAsync(card.Pid, _cts.Token).ConfigureAwait(true);
        if (_disposed)
            return;
        StatusText = focused
            ? $"Brought instance (PID {card.Pid}) to the front."
            : $"Could not bring instance {card.Pid} to the front.";
    }

    private async Task AttachInstanceAsync(InstanceCardViewModel card)
    {
        if (ReferenceEquals(SelectedInstance, card) && card.IsLinked)
        {
            await _windowCoordinator.ActivateAsync(card.Pid, _cts.Token).ConfigureAwait(true);
            return;
        }

        var generation = Interlocked.Increment(ref _selectionGeneration);
        if (SelectedInstance is { } previous)
        {
            previous.IsSelected = false;
        }

        SelectedInstance = card;
        card.IsSelected = true;
        if (card.IsLinked)
        {
            HasLinkedInstance = true;
            StatusText = $"Selected linked instance (PID {card.Pid}).";
            await _windowCoordinator.ActivateAsync(card.Pid, _cts.Token).ConfigureAwait(true);
            return;
        }

        StatusText = $"Linking instance (PID {card.Pid})…";

        var result = await _windowCoordinator.AttachAsync(card.Pid, _cts.Token).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _selectionGeneration)
            || !ReferenceEquals(SelectedInstance, card))
        {
            return;
        }

        if (result.IsAttached)
        {
            card.IsLinked = true;
            UpdateHasLinkedInstance();
            StatusText = $"Linked instance (PID {card.Pid}).";
            return;
        }

        card.IsLinked = false;
        UpdateHasLinkedInstance();
        var focused = await _focuser.FocusAsync(card.Pid, _cts.Token).ConfigureAwait(true);
        StatusText = focused
            ? $"Could not attach instance (PID {card.Pid}); focused the window instead. {result.Error}"
            : $"Could not link or focus instance {card.Pid}: {result.Error}";
    }

    [RelayCommand]
    private async Task DetachAsync()
    {
        Interlocked.Increment(ref _selectionGeneration);
        var linkedCards = EnumerateCards().Where(card => card.IsLinked).ToArray();
        await _windowCoordinator.DetachAsync(CancellationToken.None).ConfigureAwait(true);
        foreach (var card in linkedCards)
        {
            card.IsLinked = false;
        }

        if (SelectedInstance is { } selected)
        {
            selected.IsSelected = false;
        }

        SelectedInstance = null;
        HasLinkedInstance = false;
        StatusText = linkedCards.Length == 0
            ? "No linked instances."
            : $"Detached {linkedCards.Length} linked instance(s).";
    }

    private async Task DetachInstanceAsync(InstanceCardViewModel card)
    {
        if (ReferenceEquals(SelectedInstance, card))
        {
            Interlocked.Increment(ref _selectionGeneration);
            card.IsSelected = false;
            SelectedInstance = null;
        }

        await _windowCoordinator.DetachAsync(card.Pid, CancellationToken.None).ConfigureAwait(true);
        card.IsLinked = false;
        UpdateHasLinkedInstance();
        StatusText = $"Detached instance (PID {card.Pid}).";
    }

    private void OnLinkedWindowConnectionChanged(
        object? sender,
        LinkedWindowConnectionChangedEventArgs e)
    {
        var card = EnumerateCards().FirstOrDefault(candidate => candidate.Pid == e.Pid);
        if (card is null)
        {
            return;
        }

        card.IsLinked = e.IsAttached;
        UpdateHasLinkedInstance();
        if (!e.IsAttached && !string.IsNullOrWhiteSpace(e.Error))
        {
            StatusText = $"Instance {e.Pid} returned to standalone mode: {e.Error}";
        }
    }

    [RelayCommand]
    private void CollapseAll() => SetCollapsed(true);

    [RelayCommand]
    private void ExpandAll() => SetCollapsed(false);

    private void SetCollapsed(bool collapsed)
    {
        IsCollapsed = collapsed;
        foreach (var card in EnumerateCards())
            card.IsCollapsed = collapsed;
    }

    private IEnumerable<InstanceCardViewModel> EnumerateCards() => Projects.SelectMany(group => group.Instances);

    private ProjectGroupViewModel? FindGroup(InstanceCardViewModel card) =>
        Projects.FirstOrDefault(group => group.Instances.Contains(card));

    /// <summary>
    /// Puts the card in its project's card, appending it (and creating the project card at the bottom) when its project
    /// has changed.
    /// </summary>
    private void PlaceCard(InstanceCardViewModel card)
    {
        var current = FindGroup(card);
        if (current is null || current.ProjectId != card.ProjectId)
        {
            if (current is not null)
                RemoveCard(card);
            current = Projects.FirstOrDefault(group => group.ProjectId == card.ProjectId);
            if (current is null)
            {
                current = new ProjectGroupViewModel(card.ProjectId);
                Projects.Add(current);
            }
            current.Instances.Add(card);
            UpdateProjectSelection();
        }
        current.UpdateFrom(card);
    }

    /// <summary>Removes the card, dropping its project card once it is empty.</summary>
    private void RemoveCard(InstanceCardViewModel card)
    {
        if (FindGroup(card) is not { } group)
            return;
        group.Instances.Remove(card);
        if (group.Instances.Count == 0)
            Projects.Remove(group);
        UpdateProjectSelection();
    }

    partial void OnSelectedInstanceChanged(InstanceCardViewModel? value) => UpdateProjectSelection();

    /// <summary>Marks the project card holding the selected instance card.</summary>
    private void UpdateProjectSelection()
    {
        foreach (var group in Projects)
            group.HasSelectedInstance = SelectedInstance is { } selected && group.Instances.Contains(selected);
    }

    private void UpdateHasLinkedInstance() =>
        HasLinkedInstance = EnumerateCards().Any(card => card.IsLinked);

    private async Task ConfirmAndStopAsync(InstanceCardViewModel card, bool reload = false)
    {
        if (card.TurnActive || card.IsProcessActionPending || _disposed)
            return;
        card.IsProcessActionPending = true;
        try
        {
            var confirm = reload ? _confirmReload : _confirmEndProcess;
            if (confirm is not null && !await confirm(card).ConfigureAwait(true))
                return;

            // State may have changed while the confirmation dialog was open.
            if (card.TurnActive || _disposed || !EnumerateCards().Contains(card))
                return;
            var projectId = card.ProjectId;
            var conversationId = card.ConversationId;
            if (reload && !_launcher.HasLaunchTarget)
            {
                StatusText = "Cannot reload: the SharpTurns repository wasn't found. Instance was not ended.";
                return;
            }
            if (await StopInstanceAsync(card) && reload && !_disposed)
            {
                await LaunchInstanceAsync((id, registered, token) =>
                    _launcher.ReloadAsync(projectId, conversationId, id, registered, token));
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                StatusText = $"Could not {(reload ? "reload" : "end")} instance: {ex.Message}";
        }
        finally
        {
            card.IsProcessActionPending = false;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusText = "Monitoring local instances…";
        _server.Start();
        _cpuSamplingTask = Task.Run(() => SampleCpuAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task SampleCpuAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var samples = _cpuSamplers.ToArray()
                    .Select(entry => (entry.Key, entry.Value, Cpu: entry.Value.Sampler.Sample()))
                    .ToArray();
                if (samples.Length == 0)
                    continue;
                // Post rather than await: application exit synchronously waits for DisposeAsync on the UI thread.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed)
                        return;
                    foreach (var (id, sampled, cpu) in samples)
                    {
                        if (!_cpuSamplers.TryGetValue(id, out var current) || current != sampled)
                            continue;
                        var card = EnumerateCards().FirstOrDefault(candidate => candidate.Id == id);
                        if (card is not null && card.Pid == sampled.Pid)
                            card.CpuLabel = cpu is { } value ? $"CPU {value:F1}%" : "CPU —";
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnInstanceChanged(AppInstanceRecord record) =>
        Dispatcher.UIThread.Post(() => ApplyInstanceChange(record));

    private void ApplyInstanceChange(AppInstanceRecord record)
    {
        if (_disposed)
            return;
        var card = EnumerateCards().FirstOrDefault(candidate => candidate.Id == record.Id);
        if (record.Status == "stopped")
        {
            _cpuSamplers.TryRemove(record.Id, out _);
            if (card is null)
                return;
            RemoveCard(card);
            if (ReferenceEquals(SelectedInstance, card))
            {
                Interlocked.Increment(ref _selectionGeneration);
                card.IsSelected = false;
                SelectedInstance = null;
            }
            if (card.IsLinked)
                _ = DetachRemovedInstanceAsync(card.Pid);
        }
        else if (card is not null)
        {
            if (card.Pid != record.Pid)
            {
                _cpuSamplers[record.Id] = (record.Pid, new ProcessCpuSampler(record.Pid));
                card.CpuLabel = "CPU —";
            }
            card.UpdateFrom(record);
            PlaceCard(card);
        }
        else
        {
            card = new InstanceCardViewModel(record, _stopAction, _focusAction, _reloadAction, _detachAction, _attachAction)
            {
                IsCollapsed = IsCollapsed
            };
            _cpuSamplers[record.Id] = (record.Pid, new ProcessCpuSampler(record.Pid));
            PlaceCard(card);
            var pending = Volatile.Read(ref _pendingAutoAttachInstanceId);
            if (pending == record.Id
                && Interlocked.CompareExchange(ref _pendingAutoAttachInstanceId, null, pending) == pending)
                _ = AttachInstanceAsync(card);
            else
                _ = AttachDiscoveredInstanceAsync(card);
        }
        UpdateHasLinkedInstance();
        StatusText = $"Monitoring {EnumerateCards().Count()} running instance(s).";
    }

    private async Task AttachDiscoveredInstanceAsync(InstanceCardViewModel card)
    {
        try
        {
            var result = await _windowCoordinator.AttachAsync(card.Pid, activate: false, _cts.Token);
            if (_disposed || !EnumerateCards().Contains(card))
            {
                await _windowCoordinator.DetachAsync(card.Pid, CancellationToken.None);
                return;
            }
            card.IsLinked = result.IsAttached;
            UpdateHasLinkedInstance();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed)
                StatusText = $"Could not link instance {card.Pid}: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task LaunchAsync() => LaunchInstanceAsync(_launcher.LaunchAsync);

    [RelayCommand(CanExecute = nameof(CanLaunchProject))]
    private Task LaunchProjectAsync(ProjectGroupViewModel? group) =>
        group?.ProjectId is { } projectId
            ? LaunchInstanceAsync((id, registered, token) => _launcher.LaunchProjectAsync(projectId, id, registered, token))
            : Task.CompletedTask;

    private bool CanLaunchProject(ProjectGroupViewModel? group) => CanLaunch && group?.ProjectId is not null;

    private async Task LaunchInstanceAsync(Func<string, Task, CancellationToken, Task> launch)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReported(AppInstanceRecord record)
        {
            if (record.Id == instanceId && record.Status == "running")
                registered.TrySetResult();
        }

        // Subscribe before starting: a fast child can report before LaunchAsync returns.
        _server.InstanceChanged += OnReported;
        Interlocked.Exchange(ref _pendingAutoAttachInstanceId, instanceId);
        StatusText = "Launching SharpTurns; waiting for it to register…";
        try
        {
            await launch(instanceId, registered.Task, _cts.Token).ConfigureAwait(true);
            // ApplyInstanceChange owns discovery/link status. Do not overwrite it with a stale 'waiting to attach'
            // message after a fast registration.
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _pendingAutoAttachInstanceId, null, instanceId);
            if (!_disposed)
                StatusText = $"Failed to launch SharpTurns: {ex.Message}";
        }
        finally
        {
            _server.InstanceChanged -= OnReported;
        }
    }

    private async Task<bool> StopInstanceAsync(InstanceCardViewModel card)
    {
        // End the SharpTurns process tree. The reported pid is the app process (Environment.ProcessId); killing it ends
        // the app and its CLI and recorder children, and the parent `dotnet run` then exits on its own. The button is
        // only enabled while no turn or summary is running, so this only ever ends an idle instance.
        var ended = false;
        try
        {
            using var process = Process.GetProcessById(card.Pid);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                ended = true;
            }
            catch (OperationCanceledException)
            {
                ended = process.HasExited;
                if (!ended)
                    StatusText = $"Process {card.Pid} has not exited; no replacement was launched.";
            }
        }
        catch (ArgumentException)
        {
            // The process is no longer running.
            ended = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not end process {card.Pid}: {ex.Message}";
        }

        // Confirmed exit can remove the local entry immediately; crashes not initiated here are handled by the server's
        // liveness deadline.
        if (ended)
        {
            _server.MarkStopped(card.Id);
            StatusText = "Instance ended.";
        }
        return ended;
    }

    private async Task DetachRemovedInstanceAsync(int pid)
    {
        try
        {
            await _windowCoordinator.DetachAsync(pid, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The coordinator also restores the SharpTurns window by closing the channel.
        }
    }

    /// <summary>
    /// Moves a project card to <paramref name="insertBefore"/> (an index into the project list, 0..count, counted
    /// including the moved item). Used by the view's drag-to-reorder handling. No-op if the group is not listed.
    /// </summary>
    public void MoveProject(ProjectGroupViewModel group, int insertBefore) => MoveItem(Projects, group, insertBefore);

    /// <summary>
    /// Moves an instance card to <paramref name="insertBefore"/> within its own project card (0..count, counted
    /// including the moved item). Instances never move between project cards. No-op if the card is not found.
    /// </summary>
    public void MoveInstance(InstanceCardViewModel card, int insertBefore)
    {
        if (FindGroup(card) is { } group)
            MoveItem(group.Instances, card, insertBefore);
    }

    private static void MoveItem<T>(ObservableCollection<T> list, T item, int insertBefore)
    {
        var from = list.IndexOf(item);
        if (from < 0)
        {
            return;
        }

        list.RemoveAt(from);

        // insertBefore was computed against the list that still included the dragged item; compensate for its removal.
        var target = insertBefore;
        if (from < target)
        {
            target--;
        }

        target = Math.Clamp(target, 0, list.Count);
        list.Insert(target, item);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _server.InstanceChanged -= OnInstanceChanged;
        _cts.Cancel();
        if (_cpuSamplingTask is not null)
            await _cpuSamplingTask.ConfigureAwait(false);
        _cpuSamplers.Clear();
        _windowCoordinator.ConnectionChanged -= OnLinkedWindowConnectionChanged;
        await _windowCoordinator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
