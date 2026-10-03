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
/// Shows live local IPC reports as cards and coordinates window linking, as in the Workbench, with one list in place of
/// its MAIN and DEV columns.
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

    public ObservableCollection<InstanceCardViewModel> Instances { get; } = new();

    // Actions handed to each card so its buttons, and a click on the card, call back into the manager without needing a
    // relative-source DataContext binding.
    private readonly Action<InstanceCardViewModel> _stopAction;
    private readonly Action<InstanceCardViewModel> _reloadAction;
    private readonly Action<InstanceCardViewModel> _focusAction;

    // Optional confirmation gates (set by App.axaml.cs) that prompt the user before ending or reloading a process. When
    // null (e.g. in tests) the instance is ended without prompting.
    private readonly Func<InstanceCardViewModel, Task<bool>>? _confirmEndProcess;
    private readonly Func<InstanceCardViewModel, Task<bool>>? _confirmReload;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _canLaunch;
    [ObservableProperty] private InstanceCardViewModel? _selectedInstance;
    [ObservableProperty] private bool _hasLinkedInstance;

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
        CanLaunch = launcher.HasLaunchTarget;
    }

    private async Task FocusInstanceAsync(InstanceCardViewModel card)
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
            ? $"Focused instance (PID {card.Pid}); linked-window control is unavailable."
            : $"Could not link or focus instance {card.Pid}: {result.Error}";
    }

    [RelayCommand]
    private async Task DetachAsync()
    {
        Interlocked.Increment(ref _selectionGeneration);
        var linkedCards = Instances.Where(card => card.IsLinked).ToArray();
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

    private void OnLinkedWindowConnectionChanged(
        object? sender,
        LinkedWindowConnectionChangedEventArgs e)
    {
        var card = Instances.FirstOrDefault(candidate => candidate.Pid == e.Pid);
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

    private void UpdateHasLinkedInstance() =>
        HasLinkedInstance = Instances.Any(card => card.IsLinked);

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
            if (card.TurnActive || _disposed || !Instances.Contains(card))
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
                        var card = Instances.FirstOrDefault(candidate => candidate.Id == id);
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
        var card = Instances.FirstOrDefault(candidate => candidate.Id == record.Id);
        if (record.Status == "stopped")
        {
            _cpuSamplers.TryRemove(record.Id, out _);
            if (card is null)
                return;
            Instances.Remove(card);
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
        }
        else
        {
            card = new InstanceCardViewModel(record, _stopAction, _focusAction, _reloadAction);
            _cpuSamplers[record.Id] = (record.Pid, new ProcessCpuSampler(record.Pid));
            Instances.Add(card);
            var pending = Volatile.Read(ref _pendingAutoAttachInstanceId);
            if (pending == record.Id
                && Interlocked.CompareExchange(ref _pendingAutoAttachInstanceId, null, pending) == pending)
                _ = FocusInstanceAsync(card);
            else
                _ = AttachDiscoveredInstanceAsync(card);
        }
        UpdateHasLinkedInstance();
        StatusText = $"Monitoring {Instances.Count} running instance(s).";
    }

    private async Task AttachDiscoveredInstanceAsync(InstanceCardViewModel card)
    {
        try
        {
            var result = await _windowCoordinator.AttachAsync(card.Pid, activate: false, _cts.Token);
            if (_disposed || !Instances.Contains(card))
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
    /// Moves an instance card to <paramref name="insertBefore"/> (an index into the current list, 0..count, counted
    /// including the moved item). Used by the view's drag-to-reorder handling. No-op if the id is not found.
    /// </summary>
    public void MoveInstance(string instanceId, int insertBefore)
    {
        var from = -1;
        for (var i = 0; i < Instances.Count; i++)
        {
            if (Instances[i].Id == instanceId)
            {
                from = i;
                break;
            }
        }

        if (from < 0)
        {
            return;
        }

        var item = Instances[from];
        Instances.RemoveAt(from);

        // insertBefore was computed against the list that still included the dragged item; compensate for its removal.
        var target = insertBefore;
        if (from < target)
        {
            target--;
        }

        target = Math.Clamp(target, 0, Instances.Count);
        Instances.Insert(target, item);
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
