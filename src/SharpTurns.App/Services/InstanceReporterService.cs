using System.Threading.Channels;
using SharpTurns.Core.InstanceManagement;

namespace SharpTurns.App.Services;

/// <summary>What the Instance Manager shows for this instance.</summary>
public sealed record InstanceSnapshot(
    long? ProjectId,
    string? ProjectTitle,
    string? ProjectColor,
    long? ConversationId,
    string? ConversationTitle,
    string? ConversationModel,
    bool TurnActive);

/// <summary>
/// Reports this SharpTurns process's live state (selected project and open conversation, heartbeat) over local IPC, as
/// in the Workbench. A single sender coalesces state changes, retries on the next heartbeat when the manager is absent,
/// and sends stopped on shutdown.
/// </summary>
public sealed class InstanceReporterService : IAsyncDisposable
{
    private readonly Func<AppInstanceRecord, CancellationToken, Task> _reportAsync;
    private readonly string _instanceId;
    private readonly int _pid;
    private readonly Func<CancellationToken, Task<InstanceSnapshot>> _captureSnapshot;
    private InstanceSnapshot? _lastSnapshot;
    private readonly TimeSpan _heartbeatInterval;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private Task? _heartbeatLoop;
    private Task? _senderLoop;
    private DateTimeOffset _startedAt;
    private int _stopped;

    /// <summary>An instance ID that isn't a GUID in "N" format is replaced with a new one.</summary>
    public InstanceReporterService(
        Func<AppInstanceRecord, CancellationToken, Task> reportAsync,
        int pid,
        Func<CancellationToken, Task<InstanceSnapshot>> captureSnapshot,
        TimeSpan? heartbeatInterval = null,
        string? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(reportAsync);
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        _reportAsync = reportAsync;
        _instanceId = Guid.TryParseExact(instanceId, "N", out var parsedInstanceId)
            ? parsedInstanceId.ToString("N")
            : Guid.NewGuid().ToString("N");
        _pid = pid;
        _captureSnapshot = captureSnapshot;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Starts reporting without waiting for the manager to be present.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_senderLoop is not null || Volatile.Read(ref _stopped) != 0)
            throw new InvalidOperationException("Instance reporter has already started or stopped.");
        _startedAt = DateTimeOffset.UtcNow;
        _updates.Writer.TryWrite(true);
        _senderLoop = Task.Run(() => SenderLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues the latest snapshot without blocking the caller, so the Instance Manager updates without waiting for the
    /// next heartbeat.
    /// </summary>
    public void Emit()
    {
        if (Volatile.Read(ref _stopped) == 0) _updates.Writer.TryWrite(true);
    }

    private async Task SenderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await WriteAsync("running", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                _updates.Writer.TryWrite(true);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private async Task WriteAsync(string status, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            // Shutdown can run synchronously on the UI thread. Never dispatch a fresh UI capture for the stopped report.
            var snapshot = status == "stopped"
                ? _lastSnapshot
                : await _captureSnapshot(deadline.Token).ConfigureAwait(false);
            if (snapshot is null)
                return;
            _lastSnapshot = snapshot;
            var now = DateTimeOffset.UtcNow;
            var record = new AppInstanceRecord(
                Id: _instanceId,
                Pid: _pid,
                ProjectId: snapshot.ProjectId,
                ProjectTitle: snapshot.ProjectTitle,
                ConversationId: snapshot.ConversationId,
                ConversationTitle: snapshot.ConversationTitle,
                ConversationModel: snapshot.ConversationModel,
                TurnActive: snapshot.TurnActive,
                Status: status,
                TimestampStarted: _startedAt,
                HeartbeatAt: now,
                TimestampUpdated: now,
                ProjectColor: snapshot.ProjectColor);

            await _reportAsync(record, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An absent manager is normal. The next heartbeat resends full state.
        }
    }

    /// <summary>Stops the sender before sending a bounded, best-effort stopped report.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        _cts.Cancel();

        if (_heartbeatLoop is not null)
        {
            try { await _heartbeatLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (_senderLoop is not null)
        {
            try { await _senderLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await WriteAsync("stopped", cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
