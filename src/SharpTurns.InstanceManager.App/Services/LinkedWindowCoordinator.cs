using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SharpTurns.Core.InstanceManagement;

namespace SharpTurns.InstanceManager.App.Services;

public sealed record LinkedWindowAttachResult(bool IsAttached, string? Error = null);

public sealed class LinkedWindowConnectionChangedEventArgs(
    int pid,
    bool isAttached,
    string? error = null) : EventArgs
{
    public int Pid { get; } = pid;
    public bool IsAttached { get; } = isAttached;
    public string? Error { get; } = error;
}

/// <summary>
/// Coordinates the separately owned Instance Manager and attached SharpTurns
/// windows as a linked group: each SharpTurns window's left edge
/// sits on the manager's right edge, and they share one top and height. Whichever window the user moves or vertically
/// resizes is treated as the leader; programmatic follower updates are
/// suppressed to avoid loops.
/// </summary>
public sealed class LinkedWindowCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan GeometryBurstInterval = TimeSpan.FromMilliseconds(8);
    private const long GeometryBurstIdleMilliseconds = 100;
    private static readonly TimeSpan ProgrammaticEventSuppression = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private const int WindowGap = 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    private readonly Window _managerWindow;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly DispatcherTimer _managerGeometryTimer;
    private readonly object _sessionsLock = new();
    private readonly Dictionary<int, LinkedSession> _sessions = [];
    private WindowFrameBounds? _sharedAppBounds;
    private WindowFrameBounds? _activeWorkingArea;
    private int? _preferredSessionPid;
    private int _suppressionGeneration;
    private int _suppressManagerGeometryEvents;
    private long _lastManagerGeometryActivityMilliseconds;
    private bool _managerGeometryBurstActive;
    private bool _managerGeometryPending;
    private int _disposed;

    public LinkedWindowCoordinator(Window managerWindow)
    {
        ArgumentNullException.ThrowIfNull(managerWindow);
        _managerWindow = managerWindow;
        _managerGeometryTimer = new DispatcherTimer
        {
            Interval = GeometryBurstInterval,
        };
        _managerGeometryTimer.Tick += OnManagerGeometryTimerTick;
        _managerWindow.PositionChanged += OnManagerGeometryChanged;
        _managerWindow.Resized += OnManagerResized;
        _managerWindow.Activated += OnManagerActivated;
    }

    public event EventHandler<LinkedWindowConnectionChangedEventArgs>? ConnectionChanged;

    public Task<LinkedWindowAttachResult> AttachAsync(
        int pid,
        CancellationToken cancellationToken = default) =>
        AttachAsync(pid, activate: true, cancellationToken);

    public async Task<LinkedWindowAttachResult> AttachAsync(
        int pid,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var token = linkedCts.Token;

        await _transitionGate.WaitAsync(token).ConfigureAwait(false);
        WindowControlChannel? channel = null;
        LinkedSession? pendingSession = null;
        var backgroundTasksStarted = false;
        var sessionAdded = false;
        try
        {
            if (TryGetSession(pid, out var existingSession))
            {
                if (activate)
                {
                    SetPreferredSession(existingSession);
                    await SendActivateAsync(existingSession, token).ConfigureAwait(false);
                }

                return new LinkedWindowAttachResult(true);
            }

            var endpoint = WindowControlLocalIpcEndpoint.ForProcess(pid);
            var stream = await WindowControlLocalIpcClient.ConnectWithRetryAsync(endpoint, token)
                .ConfigureAwait(false);
            channel = new WindowControlChannel(stream);

            var ready = await ReadWithTimeoutAsync(channel, token).ConfigureAwait(false);
            if (ready is null
                || ready.Type != WindowControlMessageTypes.Ready
                || ready.Pid != pid
                || ready.Bounds is null)
            {
                throw new InvalidDataException("SharpTurns returned an invalid window-control handshake.");
            }

            var pair = await InvokeOnUiThreadAsync(() => CreateInitialPair(ready)).ConfigureAwait(false);
            var session = new LinkedSession(
                pid,
                Guid.NewGuid().ToString("N"),
                channel,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token),
                ready.MinimumWidth.GetValueOrDefault(ready.Bounds.Width));
            pendingSession = session;
            channel = null;

            var sequence = Interlocked.Increment(ref session.Sequence);
            await session.Channel.SendAsync(
                new WindowControlMessage(
                    WindowControlMessageTypes.Attach,
                    SessionId: session.SessionId,
                    Sequence: sequence,
                    Bounds: pair.App),
                token).ConfigureAwait(false);

            var attached = await ReadWithTimeoutAsync(session.Channel, token).ConfigureAwait(false);
            if (attached is null
                || attached.Type != WindowControlMessageTypes.Attached
                || attached.Pid != pid
                || !string.Equals(attached.SessionId, session.SessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SharpTurns did not acknowledge linked-window mode.");
            }

            await InvokeOnUiThreadAsync(() =>
            {
                if (AvaloniaWindowFrame.Capture(_managerWindow) != pair.Manager)
                {
                    SuppressProgrammaticManagerGeometryEvents();
                    AvaloniaWindowFrame.Apply(_managerWindow, pair.Manager);
                }
            }).ConfigureAwait(false);

            AddSession(session, pair.App);
            sessionAdded = true;
            session.StartBackgroundTasks(
                () => ReadLoopAsync(session),
                () => HeartbeatLoopAsync(session));
            backgroundTasksStarted = true;

            SendBoundsToFollowers(session, pair.App);

            if (activate)
            {
                await SendActivateAsync(session, token).ConfigureAwait(false);
            }

            await RaiseConnectionChangedAsync(pid, isAttached: true, error: null).ConfigureAwait(false);
            pendingSession = null;
            return new LinkedWindowAttachResult(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await CloseFailedAttachAsync(channel, pendingSession, backgroundTasksStarted, sessionAdded).ConfigureAwait(false);
            return new LinkedWindowAttachResult(false, "Window linking was canceled.");
        }
        catch (Exception ex)
        {
            await CloseFailedAttachAsync(channel, pendingSession, backgroundTasksStarted, sessionAdded).ConfigureAwait(false);
            return new LinkedWindowAttachResult(false, ex.Message);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task CloseFailedAttachAsync(
        WindowControlChannel? channel,
        LinkedSession? session,
        bool hasBackgroundTasks,
        bool sessionAdded)
    {
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (session is null)
        {
            return;
        }

        if (sessionAdded)
        {
            RemoveSession(session);
        }

        await session.CloseAsync(hasBackgroundTasks).ConfigureAwait(false);
    }

    public async Task ActivateAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(pid, out var session))
        {
            return;
        }

        if (!SetPreferredSession(session))
        {
            return;
        }

        try
        {
            await SendActivateAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await HandleUnexpectedDisconnectAsync(session, ex.Message).ConfigureAwait(false);
        }
    }

    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var session in GetSessionsSnapshot())
            {
                await DetachSessionCoreAsync(session, cancellationToken, notify: true).ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task DetachAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetSession(pid, out var session))
            {
                await DetachSessionCoreAsync(session, cancellationToken, notify: true).ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private LinkedWindowPair CreateInitialPair(WindowControlMessage ready)
    {
        var managerBounds = AvaloniaWindowFrame.Capture(_managerWindow);
        var sharedAppBounds = GetSharedAppBounds();
        var workingArea = AvaloniaWindowFrame.CaptureWorkingArea(_managerWindow, managerBounds);
        if (sharedAppBounds is not null)
        {
            var minimumAppWidth = Math.Max(
                GetMinimumAppWidth(sharedAppBounds.Width),
                ready.MinimumWidth.GetValueOrDefault(ready.Bounds!.Width));
            var pair = GetActiveWorkingArea() == workingArea
                ? LinkedWindowLayout.FitVertically(
                    managerBounds,
                    sharedAppBounds,
                    workingArea,
                    WindowGap)
                : LinkedWindowLayout.CreateInitial(
                    managerBounds,
                    sharedAppBounds,
                    workingArea,
                    WindowGap,
                    minimumAppWidth);
            SetActiveWorkingArea(workingArea);
            return pair;
        }

        if (ready.MinimumHeight is > 0)
        {
            managerBounds = managerBounds with
            {
                Height = Math.Max(managerBounds.Height, ready.MinimumHeight.Value),
            };
        }

        var appBounds = ready.Bounds!;
        if (ready.MinimumWidth is > 0)
        {
            appBounds = appBounds with
            {
                Width = Math.Max(appBounds.Width, ready.MinimumWidth.Value),
            };
        }

        var initialPair = LinkedWindowLayout.CreateInitial(
            managerBounds,
            appBounds,
            workingArea,
            WindowGap,
            ready.MinimumWidth is > 0 ? ready.MinimumWidth.Value : ready.Bounds!.Width);
        SetActiveWorkingArea(workingArea);
        return initialPair;
    }

    private async Task<WindowControlMessage?> ReadWithTimeoutAsync(
        WindowControlChannel channel,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeout);
        return await channel.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(LinkedSession session)
    {
        string? disconnectError = null;
        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                var message = await session.Channel.ReadAsync(session.Cancellation.Token).ConfigureAwait(false);
                if (message is null || message.Type == WindowControlMessageTypes.Detached)
                {
                    break;
                }

                switch (message.Type)
                {
                    case WindowControlMessageTypes.GeometryChanged
                        when message.Bounds is not null
                             && string.Equals(message.SessionId, session.SessionId, StringComparison.Ordinal):
                        await ApplyAppGeometryAsync(session, message.Bounds).ConfigureAwait(false);
                        break;

                    case WindowControlMessageTypes.WindowActivated
                        when string.Equals(message.SessionId, session.SessionId, StringComparison.Ordinal):
                        await HandleAppActivatedAsync(session).ConfigureAwait(false);
                        break;

                    case WindowControlMessageTypes.Error:
                        disconnectError = message.Error ?? "SharpTurns rejected a window-control command.";
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            disconnectError = ex.Message;
        }
        finally
        {
            if (!session.Cancellation.IsCancellationRequested)
            {
                await HandleUnexpectedDisconnectAsync(
                    session,
                    disconnectError ?? "The SharpTurns window-control connection closed.")
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync(LinkedSession session)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cancellation.Token).ConfigureAwait(false))
            {
                await session.Channel.SendAsync(
                    new WindowControlMessage(
                        WindowControlMessageTypes.Ping,
                        SessionId: session.SessionId,
                        Sequence: Interlocked.Read(ref session.Sequence)),
                    session.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Expected when switching instances or exiting.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await HandleUnexpectedDisconnectAsync(session, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task ApplyAppGeometryAsync(
        LinkedSession session,
        WindowFrameBounds appBounds)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            if (!ContainsSession(session))
            {
                return;
            }

            var managerBounds = AvaloniaWindowFrame.Capture(_managerWindow);
            var targetManager = LinkedWindowLayout.AnchorManager(managerBounds, appBounds, WindowGap);
            var workingArea = AvaloniaWindowFrame.CaptureWorkingArea(_managerWindow, targetManager);
            var pair = GetActiveWorkingArea() == workingArea
                ? LinkedWindowLayout.FitVertically(targetManager, appBounds, workingArea, WindowGap)
                : LinkedWindowLayout.CreateInitial(
                    targetManager,
                    appBounds,
                    workingArea,
                    WindowGap,
                    GetMinimumAppWidth(appBounds.Width));
            SetActiveWorkingArea(workingArea);
            SetSharedAppBounds(pair.App);
            if (managerBounds != pair.Manager)
            {
                SuppressProgrammaticManagerGeometryEvents();
                AvaloniaWindowFrame.Apply(_managerWindow, pair.Manager);
            }

            if (pair.App != appBounds)
            {
                SendBoundsToAll(pair.App);
            }
            else
            {
                SendBoundsToFollowers(session, pair.App);
            }
        }).ConfigureAwait(false);
    }

    private void OnManagerGeometryChanged(object? sender, PixelPointEventArgs e) => QueueManagerGeometryChanged();

    private void OnManagerResized(object? sender, WindowResizedEventArgs e) => QueueManagerGeometryChanged();

    private void OnManagerActivated(object? sender, EventArgs e)
    {
        if (GetPreferredSession() is { } session)
        {
            _ = RaiseAppWithoutActivationAsync(session);
        }
    }

    private Task HandleAppActivatedAsync(LinkedSession session) =>
        InvokeOnUiThreadAsync(() =>
        {
            if (!SetPreferredSession(session))
            {
                return;
            }

            if (_managerWindow.WindowState == WindowState.Minimized)
            {
                _managerWindow.WindowState = WindowState.Normal;
            }

            var handle = _managerWindow.TryGetPlatformHandle();
            if (handle is not null)
            {
                _ = WindowZOrder.TryRaiseWithoutActivation(handle.Handle, handle.HandleDescriptor);
            }
        });

    private void QueueManagerGeometryChanged()
    {
        if (!HasSessions() || Volatile.Read(ref _suppressManagerGeometryEvents) != 0)
        {
            return;
        }

        _lastManagerGeometryActivityMilliseconds = Environment.TickCount64;
        _managerGeometryPending = true;
        if (!_managerGeometryBurstActive)
        {
            _managerGeometryBurstActive = true;
            ApplyPendingManagerGeometry();
            _managerGeometryTimer.Start();
        }
    }

    private void OnManagerGeometryTimerTick(object? sender, EventArgs e)
    {
        ApplyPendingManagerGeometry();
        if (Environment.TickCount64 - _lastManagerGeometryActivityMilliseconds >= GeometryBurstIdleMilliseconds)
        {
            StopManagerGeometryBurst();
        }
    }

    private void ApplyPendingManagerGeometry()
    {
        if (!_managerGeometryPending)
        {
            return;
        }

        _managerGeometryPending = false;
        var appBounds = GetSharedAppBounds();
        if (appBounds is null || Volatile.Read(ref _suppressManagerGeometryEvents) != 0)
        {
            return;
        }

        var managerBounds = AvaloniaWindowFrame.Capture(_managerWindow);
        var workingArea = AvaloniaWindowFrame.CaptureWorkingArea(_managerWindow, managerBounds);
        var pair = GetActiveWorkingArea() == workingArea
            ? LinkedWindowLayout.FitVertically(managerBounds, appBounds, workingArea, WindowGap)
            : LinkedWindowLayout.CreateInitial(
                managerBounds,
                appBounds,
                workingArea,
                WindowGap,
                GetMinimumAppWidth(appBounds.Width));
        SetActiveWorkingArea(workingArea);
        SetSharedAppBounds(pair.App);
        if (managerBounds != pair.Manager)
        {
            SuppressProgrammaticManagerGeometryEvents();
            AvaloniaWindowFrame.Apply(_managerWindow, pair.Manager);
        }

        SendBoundsToAll(pair.App);
    }

    private void StopManagerGeometryBurst()
    {
        _managerGeometryTimer.Stop();
        _managerGeometryBurstActive = false;
        _managerGeometryPending = false;
    }

    private void SendBoundsToAll(WindowFrameBounds bounds)
    {
        foreach (var session in GetSessionsSnapshot())
        {
            var sequence = Interlocked.Increment(ref session.Sequence);
            _ = SendBoundsAsync(session, bounds, sequence);
        }
    }

    private void SendBoundsToFollowers(LinkedSession leader, WindowFrameBounds bounds)
    {
        foreach (var session in GetSessionsSnapshot())
        {
            if (ReferenceEquals(session, leader))
            {
                continue;
            }

            var sequence = Interlocked.Increment(ref session.Sequence);
            _ = SendBoundsAsync(session, bounds, sequence);
        }
    }

    private async Task SendBoundsAsync(
        LinkedSession session,
        WindowFrameBounds bounds,
        long sequence)
    {
        try
        {
            await session.Channel.SendAsync(
                new WindowControlMessage(
                    WindowControlMessageTypes.SetBounds,
                    SessionId: session.SessionId,
                    Sequence: sequence,
                    Bounds: bounds),
                session.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Expected when switching instances or exiting.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await HandleUnexpectedDisconnectAsync(session, ex.Message).ConfigureAwait(false);
        }
    }

    private void SuppressProgrammaticManagerGeometryEvents()
    {
        var generation = Interlocked.Increment(ref _suppressionGeneration);
        Volatile.Write(ref _suppressManagerGeometryEvents, 1);
        _ = ClearManagerSuppressionAsync(generation);
    }

    private async Task ClearManagerSuppressionAsync(int generation)
    {
        try
        {
            await Task.Delay(ProgrammaticEventSuppression, _lifetimeCts.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _suppressionGeneration) == generation)
            {
                Volatile.Write(ref _suppressManagerGeometryEvents, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task HandleUnexpectedDisconnectAsync(LinkedSession session, string error)
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!RemoveSession(session))
            {
                return;
            }

            await session.CloseAsync(hasBackgroundTasks: true).ConfigureAwait(false);
            await RaiseConnectionChangedAsync(session.Pid, isAttached: false, error).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task DetachSessionCoreAsync(
        LinkedSession session,
        CancellationToken cancellationToken,
        bool notify)
    {
        if (!RemoveSession(session))
        {
            return;
        }

        try
        {
            await session.Channel.SendAsync(
                new WindowControlMessage(
                    WindowControlMessageTypes.Detach,
                    SessionId: session.SessionId,
                    Sequence: Interlocked.Increment(ref session.Sequence)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Closing the channel also makes SharpTurns restore its standalone window.
        }
        finally
        {
            await session.CloseAsync(hasBackgroundTasks: true).ConfigureAwait(false);
        }

        if (notify)
        {
            await RaiseConnectionChangedAsync(session.Pid, isAttached: false, error: null).ConfigureAwait(false);
        }
    }

    private static ValueTask SendActivateAsync(
        LinkedSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            // The manager receives the click, but the background SharpTurns calls
            // Activate. Transfer foreground permission to that PID before IPC.
            // Best-effort: Windows may deny this if the user has switched away.
            _ = AllowSetForegroundWindow((uint)session.Pid);
        }

        return session.Channel.SendAsync(
            new WindowControlMessage(
                WindowControlMessageTypes.Activate,
                SessionId: session.SessionId,
                Sequence: Interlocked.Read(ref session.Sequence)),
            cancellationToken);
    }

    private async Task RaiseAppWithoutActivationAsync(LinkedSession session)
    {
        try
        {
            await session.Channel.SendAsync(
                new WindowControlMessage(
                    WindowControlMessageTypes.Raise,
                    SessionId: session.SessionId,
                    Sequence: Interlocked.Read(ref session.Sequence)),
                session.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Expected when an attached instance exits or is detached.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await HandleUnexpectedDisconnectAsync(session, ex.Message).ConfigureAwait(false);
        }
    }

    private void AddSession(LinkedSession session, WindowFrameBounds appBounds)
    {
        lock (_sessionsLock)
        {
            _sessions.Add(session.Pid, session);
            _sharedAppBounds = appBounds;
            _preferredSessionPid = session.Pid;
        }
    }

    private bool RemoveSession(LinkedSession session)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(session.Pid, out var current)
                || !ReferenceEquals(current, session))
            {
                return false;
            }

            _sessions.Remove(session.Pid);
            if (_preferredSessionPid == session.Pid)
            {
                _preferredSessionPid = _sessions.Count == 0 ? null : _sessions.Keys.First();
            }

            if (_sessions.Count == 0)
            {
                _sharedAppBounds = null;
                _activeWorkingArea = null;
                Dispatcher.UIThread.Post(StopManagerGeometryBurst);
            }

            return true;
        }
    }

    private bool TryGetSession(int pid, out LinkedSession session)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(pid, out session!);
        }
    }

    private bool ContainsSession(LinkedSession session)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(session.Pid, out var current)
                && ReferenceEquals(current, session);
        }
    }

    private bool SetPreferredSession(LinkedSession session)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(session.Pid, out var current)
                || !ReferenceEquals(current, session))
            {
                return false;
            }

            _preferredSessionPid = session.Pid;
            return true;
        }
    }

    private LinkedSession? GetPreferredSession()
    {
        lock (_sessionsLock)
        {
            return _preferredSessionPid is { } pid
                   && _sessions.TryGetValue(pid, out var session)
                ? session
                : null;
        }
    }

    private bool HasSessions()
    {
        lock (_sessionsLock)
        {
            return _sessions.Count > 0;
        }
    }

    private LinkedSession[] GetSessionsSnapshot()
    {
        lock (_sessionsLock)
        {
            return [.. _sessions.Values];
        }
    }

    private WindowFrameBounds? GetSharedAppBounds()
    {
        lock (_sessionsLock)
        {
            return _sharedAppBounds;
        }
    }

    private void SetSharedAppBounds(WindowFrameBounds bounds)
    {
        lock (_sessionsLock)
        {
            _sharedAppBounds = bounds;
        }
    }

    private int GetMinimumAppWidth(int fallback)
    {
        lock (_sessionsLock)
        {
            return _sessions.Count == 0
                ? fallback
                : _sessions.Values.Max(session => session.MinimumWidth);
        }
    }

    private WindowFrameBounds? GetActiveWorkingArea()
    {
        lock (_sessionsLock)
        {
            return _activeWorkingArea;
        }
    }

    private void SetActiveWorkingArea(WindowFrameBounds workingArea)
    {
        lock (_sessionsLock)
        {
            _activeWorkingArea = workingArea;
        }
    }

    private Task RaiseConnectionChangedAsync(int pid, bool isAttached, string? error) =>
        InvokeOnUiThreadAsync(() =>
            ConnectionChanged?.Invoke(
                this,
                new LinkedWindowConnectionChangedEventArgs(pid, isAttached, error)));

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task InvokeOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _managerWindow.PositionChanged -= OnManagerGeometryChanged;
        _managerWindow.Resized -= OnManagerResized;
        _managerWindow.Activated -= OnManagerActivated;
        StopManagerGeometryBurst();

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in GetSessionsSnapshot())
            {
                await DetachSessionCoreAsync(session, CancellationToken.None, notify: false).ConfigureAwait(false);
            }

            _lifetimeCts.Cancel();
        }
        finally
        {
            _transitionGate.Release();
        }

        _lifetimeCts.Dispose();
        _transitionGate.Dispose();
    }

    private sealed class LinkedSession(
        int pid,
        string sessionId,
        WindowControlChannel channel,
        CancellationTokenSource cancellation,
        int minimumWidth)
    {
        public int Pid { get; } = pid;
        public string SessionId { get; } = sessionId;
        public WindowControlChannel Channel { get; } = channel;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int MinimumWidth { get; } = minimumWidth;
        public long Sequence;

        private readonly TaskCompletionSource _backgroundTasksStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _readTask;
        private Task? _heartbeatTask;
        private int _closeStarted;

        public void StartBackgroundTasks(Func<Task> readLoop, Func<Task> heartbeatLoop)
        {
            _readTask = Task.Run(readLoop);
            _heartbeatTask = Task.Run(heartbeatLoop);
            _backgroundTasksStarted.TrySetResult();
        }

        public async ValueTask CloseAsync(bool hasBackgroundTasks)
        {
            if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            {
                return;
            }

            Cancellation.Cancel();
            try
            {
                await Channel.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (hasBackgroundTasks)
                {
                    _ = DisposeCancellationWhenBackgroundTasksCompleteAsync();
                }
                else
                {
                    Cancellation.Dispose();
                }
            }
        }

        private async Task DisposeCancellationWhenBackgroundTasksCompleteAsync()
        {
            try
            {
                await _backgroundTasksStarted.Task.ConfigureAwait(false);
                await Task.WhenAll(_readTask!, _heartbeatTask!).ConfigureAwait(false);
            }
            catch
            {
                // The loops own their operational errors; this path only releases resources.
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }

    private static class AvaloniaWindowFrame
    {
        public static WindowFrameBounds Capture(Window window)
        {
            var scale = PositiveScale(window.DesktopScaling);
            var frame = window.FrameSize ?? window.ClientSize;
            var bounds = new WindowFrameBounds(
                window.Position.X,
                window.Position.Y,
                Math.Max(1, (int)Math.Round(frame.Width * scale)),
                Math.Max(1, (int)Math.Round(frame.Height * scale)));
            return WindowsVisibleFrame.ToVisibleFrameBounds(NativeHandle(window), bounds);
        }

        public static WindowFrameBounds CaptureWorkingArea(Window window, WindowFrameBounds bounds)
        {
            var center = new PixelPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var screen = window.Screens.ScreenFromPoint(center) ?? window.Screens.Primary;
            var area = screen?.WorkingArea ?? new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return new WindowFrameBounds(area.X, area.Y, Math.Max(1, area.Width), Math.Max(1, area.Height));
        }

        public static void Apply(Window window, WindowFrameBounds bounds)
        {
            bounds = WindowsVisibleFrame.FromVisibleFrameBounds(NativeHandle(window), bounds);
            var center = new PixelPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var scale = PositiveScale(window.Screens.ScreenFromPoint(center)?.Scaling ?? window.DesktopScaling);
            var frame = window.FrameSize ?? window.ClientSize;
            var chromeWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
            var chromeHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
            var clientWidth = Math.Max(window.MinWidth, bounds.Width / scale - chromeWidth);
            var clientHeight = Math.Max(window.MinHeight, bounds.Height / scale - chromeHeight);

            window.Position = new PixelPoint(bounds.X, bounds.Y);
            window.Width = clientWidth;
            window.Height = clientHeight;
        }

        private static double PositiveScale(double scale) => scale > 0 ? scale : 1;

        private static IntPtr NativeHandle(Window window) => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }
}
