using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// Owns the per-user manager endpoint and an ephemeral registry. Reports are short
/// request/ack exchanges; a fresh snapshot on every heartbeat recovers manager restarts.
/// </summary>
public sealed class InstanceManagerServer : IAsyncDisposable
{
    private readonly Mutex _ownership;
    private readonly WindowControlLocalIpcListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (AppInstanceRecord Record, long Seen)> _instances = new();
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private Task? _listenTask;
    private Task? _pruneTask;
    private int _disposed;

    public event Action<AppInstanceRecord>? InstanceChanged;
    public event Action? FocusRequested;

    private InstanceManagerServer(Mutex ownership, WindowControlLocalIpcListener listener, TimeProvider timeProvider)
    {
        _ownership = ownership;
        _listener = listener;
        _timeProvider = timeProvider;
    }

    public static InstanceManagerServer? TryCreate(
        WindowControlLocalIpcEndpoint? endpoint = null,
        TimeProvider? timeProvider = null)
    {
        endpoint ??= WindowControlLocalIpcEndpoint.ForInstanceManager();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Address)))[..24];
        // The named object is kept alive, not acquired by a thread, so disposal is
        // safe on another thread. It also guards stale Unix socket cleanup in Bind.
        var ownership = new Mutex(false, $"SharpTurns.InstanceManager.{hash}", out var createdNew);
        if (!createdNew)
        {
            ownership.Dispose();
            return null;
        }

        try
        {
            return new(ownership, WindowControlLocalIpcListener.Bind(endpoint), timeProvider ?? TimeProvider.System);
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    public void Start()
    {
        if (_listenTask is not null)
            throw new InvalidOperationException("Instance manager listener is already started.");
        _listenTask = Task.Run(() => ListenAsync(_cts.Token));
        _pruneTask = Task.Run(() => PruneAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var stream = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var message = await InstanceManagerProtocol.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
                switch (message.Type)
                {
                    case "report":
                        Apply(message.Instance!);
                        break;
                    case "focus":
                        FocusRequested?.Invoke();
                        break;
                    default:
                        throw new InvalidDataException("Expected a report or focus request.");
                }
                await InstanceManagerProtocol.WriteAsync(stream, new("ack"), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException
                or JsonException or OperationCanceledException)
            {
                // A malformed, disconnected or stalled client must not stop discovery.
                Trace.WriteLine($"Instance-manager IPC request failed: {ex.GetType().Name}");
            }
        }
    }

    public void MarkStopped(string instanceId)
    {
        lock (_gate)
        {
            if (_instances.Remove(instanceId, out var removed))
                InstanceChanged?.Invoke(removed.Record with { Status = "stopped" });
        }
    }

    private void Apply(AppInstanceRecord record)
    {
        lock (_gate)
        {
            if (record.Status == "stopped")
            {
                MarkStopped(record.Id);
                return;
            }

            // Treat receipt, not the remote wall clock, as proof of liveness.
            var now = _timeProvider.GetUtcNow();
            record = record with { HeartbeatAt = now, TimestampUpdated = now };
            var changed = !_instances.TryGetValue(record.Id, out var previous)
                || previous.Record with { HeartbeatAt = now, TimestampUpdated = now } != record;
            _instances[record.Id] = (record, _timeProvider.GetTimestamp());
            if (changed)
                InstanceChanged?.Invoke(record);
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    var now = _timeProvider.GetTimestamp();
                    foreach (var (id, entry) in _instances.ToArray())
                    {
                        if (_timeProvider.GetElapsedTime(entry.Seen, now) <= TimeSpan.FromSeconds(20))
                            continue;
                        MarkStopped(id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        try
        {
            if (_listenTask is not null) await _listenTask.ConfigureAwait(false);
            if (_pruneTask is not null) await _pruneTask.ConfigureAwait(false);
        }
        finally
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _ownership.Dispose();
            _cts.Dispose();
        }
    }
}
