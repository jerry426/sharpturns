using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SharpTurns.Core.InstanceManagement;

namespace SharpTurns.App.Services;

/// <summary>
/// Lets the current-user Instance Manager coordinate this SharpTurns window while
/// SharpTurns remains the owner of its native top-level window and process.
/// </summary>
public sealed class WindowControlHost : IAsyncDisposable
{
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan GeometryBurstInterval = TimeSpan.FromMilliseconds(8);
    private const long GeometryBurstIdleMilliseconds = 100;
    private static readonly TimeSpan ProgrammaticEventSuppression = TimeSpan.FromMilliseconds(120);

    private readonly Window _window;
    private readonly int _pid;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _geometryTimer;
    private WindowControlLocalIpcListener? _listener;
    private WindowControlChannel? _activeChannel;
    private Task? _acceptLoop;
    private StandaloneWindowState? _standaloneState;
    private string? _sessionId;
    private long _lastSequence;
    private int _suppressionGeneration;
    private int _suppressGeometryEvents;
    private long _lastGeometryActivityMilliseconds;
    private bool _started;
    private bool _attached;
    private bool _geometryBurstActive;
    private bool _geometryPending;
    private bool _restoreOnStop = true;

    public WindowControlHost(Window window, int pid)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        _window = window;
        _pid = pid;
        _geometryTimer = new DispatcherTimer
        {
            Interval = GeometryBurstInterval,
        };
        _geometryTimer.Tick += OnGeometryTimerTick;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            _listener = WindowControlLocalIpcListener.Bind(WindowControlLocalIpcEndpoint.ForProcess(_pid));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SharpTurns window-control endpoint could not start: {ex.Message}");
            return;
        }

        _window.PositionChanged += OnWindowGeometryChanged;
        _window.Resized += OnWindowResized;
        _window.Activated += OnWindowActivated;
        _started = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Cancels IPC without attempting a visual restore while the application is
    /// already closing. A manager-side disconnect during normal operation still
    /// restores the standalone window automatically.
    /// </summary>
    public void StopForApplicationExit()
    {
        _restoreOnStop = false;
        StopCore();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var stream = await _listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SharpTurns window-control connection failed: {ex.Message}");
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var channel = new WindowControlChannel(stream);
        _activeChannel = channel;
        try
        {
            var ready = await InvokeOnUiThreadAsync(() =>
            {
                var bounds = AvaloniaWindowFrame.Capture(_window);
                var minimum = AvaloniaWindowFrame.CaptureMinimum(_window);
                return new WindowControlMessage(
                    WindowControlMessageTypes.Ready,
                    Bounds: bounds,
                    Pid: _pid,
                    MinimumWidth: minimum.Width,
                    MinimumHeight: minimum.Height);
            }).ConfigureAwait(false);
            await channel.SendAsync(ready, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(ConnectionIdleTimeout);
                var message = await channel.ReadAsync(idleCts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleMessageAsync(channel, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (OperationCanceledException)
        {
            // The manager stopped sending heartbeats. Treat it as disconnected.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SharpTurns window-control session ended: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activeChannel, channel))
            {
                _activeChannel = null;
            }

            if (_restoreOnStop)
            {
                await InvokeOnUiThreadAsync(DetachCore).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleMessageAsync(
        WindowControlChannel channel,
        WindowControlMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case WindowControlMessageTypes.Attach:
                if (string.IsNullOrWhiteSpace(message.SessionId) || message.Bounds is null)
                {
                    await SendErrorAsync(channel, "Attach requires a session and bounds.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                await InvokeOnUiThreadAsync(() => AttachCore(message.SessionId, message.Bounds, message.Sequence))
                    .ConfigureAwait(false);
                await channel.SendAsync(
                    new WindowControlMessage(
                        WindowControlMessageTypes.Attached,
                        SessionId: message.SessionId,
                        Sequence: message.Sequence,
                        Bounds: message.Bounds,
                        Pid: _pid),
                    cancellationToken).ConfigureAwait(false);
                break;

            case WindowControlMessageTypes.SetBounds:
                if (!IsCurrentSession(message) || message.Bounds is null)
                {
                    return;
                }

                await InvokeOnUiThreadAsync(() => ApplyManagedBounds(message.Bounds, message.Sequence))
                    .ConfigureAwait(false);
                break;

            case WindowControlMessageTypes.Activate:
                if (IsCurrentSession(message))
                {
                    await InvokeOnUiThreadAsync(() =>
                    {
                        if (_window.WindowState == WindowState.Minimized)
                        {
                            _window.WindowState = WindowState.Normal;
                        }

                        _window.Activate();
                    }).ConfigureAwait(false);
                }
                break;

            case WindowControlMessageTypes.Raise:
                if (IsCurrentSession(message))
                {
                    await InvokeOnUiThreadAsync(RaiseWindowWithoutActivation).ConfigureAwait(false);
                }
                break;

            case WindowControlMessageTypes.Detach:
                if (IsCurrentSession(message))
                {
                    await InvokeOnUiThreadAsync(DetachCore).ConfigureAwait(false);
                    await channel.SendAsync(
                        new WindowControlMessage(
                            WindowControlMessageTypes.Detached,
                            SessionId: message.SessionId,
                            Sequence: message.Sequence,
                            Pid: _pid),
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case WindowControlMessageTypes.Ping:
                await channel.SendAsync(
                    new WindowControlMessage(
                        WindowControlMessageTypes.Pong,
                        SessionId: message.SessionId,
                        Sequence: message.Sequence,
                        Pid: _pid),
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void AttachCore(string sessionId, WindowFrameBounds bounds, long sequence)
    {
        if (!_attached)
        {
            _standaloneState = new StandaloneWindowState(
                AvaloniaWindowFrame.Capture(_window),
                _window.WindowState);
        }

        _sessionId = sessionId;
        _attached = true;
        ApplyManagedBounds(bounds, sequence);
    }

    private void ApplyManagedBounds(WindowFrameBounds bounds, long sequence)
    {
        if (!_attached || sequence < _lastSequence)
        {
            return;
        }

        _lastSequence = sequence;
        SuppressProgrammaticGeometryEvents();
        if (_window.WindowState != WindowState.Normal)
        {
            _window.WindowState = WindowState.Normal;
        }

        AvaloniaWindowFrame.Apply(_window, bounds);
    }

    private void DetachCore()
    {
        StopGeometryBurst();
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _sessionId = null;
        _lastSequence = 0;
        SuppressProgrammaticGeometryEvents();
        if (_standaloneState is { } standalone)
        {
            _window.WindowState = WindowState.Normal;
            AvaloniaWindowFrame.Apply(_window, standalone.Bounds);
            _window.WindowState = standalone.State;
        }

        _standaloneState = null;
    }

    private bool IsCurrentSession(WindowControlMessage message) =>
        _attached
        && !string.IsNullOrWhiteSpace(message.SessionId)
        && string.Equals(message.SessionId, _sessionId, StringComparison.Ordinal);

    private void OnWindowGeometryChanged(object? sender, PixelPointEventArgs e) => QueueGeometryChanged();

    private void OnWindowResized(object? sender, WindowResizedEventArgs e) => QueueGeometryChanged();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!_attached || _activeChannel is null || _sessionId is null)
        {
            return;
        }

        var message = new WindowControlMessage(
            WindowControlMessageTypes.WindowActivated,
            SessionId: _sessionId,
            Sequence: Interlocked.Read(ref _lastSequence),
            Pid: _pid);
        _ = SendNotificationAsync(_activeChannel, message, _cts.Token);
    }

    private void RaiseWindowWithoutActivation()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        var handle = _window.TryGetPlatformHandle();
        if (handle is not null)
        {
            _ = WindowZOrder.TryRaiseWithoutActivation(handle.Handle, handle.HandleDescriptor);
        }
    }

    private void QueueGeometryChanged()
    {
        if (!_attached || Volatile.Read(ref _suppressGeometryEvents) != 0)
        {
            return;
        }

        _lastGeometryActivityMilliseconds = Environment.TickCount64;
        _geometryPending = true;
        if (!_geometryBurstActive)
        {
            _geometryBurstActive = true;
            SendPendingGeometryChanged();
            _geometryTimer.Start();
        }
    }

    private void OnGeometryTimerTick(object? sender, EventArgs e)
    {
        SendPendingGeometryChanged();
        if (Environment.TickCount64 - _lastGeometryActivityMilliseconds >= GeometryBurstIdleMilliseconds)
        {
            StopGeometryBurst();
        }
    }

    private void SendPendingGeometryChanged()
    {
        if (!_geometryPending)
        {
            return;
        }

        _geometryPending = false;
        if (!_attached || _activeChannel is null || _sessionId is null)
        {
            return;
        }

        var message = new WindowControlMessage(
            WindowControlMessageTypes.GeometryChanged,
            SessionId: _sessionId,
            Sequence: _lastSequence,
            Bounds: AvaloniaWindowFrame.Capture(_window),
            Pid: _pid);
        _ = SendNotificationAsync(_activeChannel, message, _cts.Token);
    }

    private void StopGeometryBurst()
    {
        _geometryTimer.Stop();
        _geometryBurstActive = false;
        _geometryPending = false;
    }

    private static async Task SendNotificationAsync(
        WindowControlChannel channel,
        WindowControlMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The read loop owns disconnect handling and standalone restoration.
        }
    }

    private void SuppressProgrammaticGeometryEvents()
    {
        var generation = Interlocked.Increment(ref _suppressionGeneration);
        Volatile.Write(ref _suppressGeometryEvents, 1);
        _ = ClearSuppressionAsync(generation);
    }

    private async Task ClearSuppressionAsync(int generation)
    {
        try
        {
            await Task.Delay(ProgrammaticEventSuppression, _cts.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _suppressionGeneration) == generation)
            {
                Volatile.Write(ref _suppressGeometryEvents, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private static ValueTask SendErrorAsync(
        WindowControlChannel channel,
        string error,
        CancellationToken cancellationToken) =>
        channel.SendAsync(new WindowControlMessage(WindowControlMessageTypes.Error, Error: error), cancellationToken);

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

    private void StopCore()
    {
        if (!_started)
        {
            return;
        }

        _window.PositionChanged -= OnWindowGeometryChanged;
        _window.Resized -= OnWindowResized;
        _window.Activated -= OnWindowActivated;
        StopGeometryBurst();
        _cts.Cancel();
        _ = _listener?.DisposeAsync();
        _ = _activeChannel?.DisposeAsync();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        StopCore();
        if (_restoreOnStop)
        {
            await InvokeOnUiThreadAsync(DetachCore).ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        _cts.Dispose();
    }

    private sealed record StandaloneWindowState(WindowFrameBounds Bounds, WindowState State);

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

        public static (int Width, int Height) CaptureMinimum(Window window)
        {
            var scale = PositiveScale(window.DesktopScaling);
            var frame = window.FrameSize ?? window.ClientSize;
            var chromeWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
            var chromeHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
            var bounds = WindowsVisibleFrame.ToVisibleFrameBounds(
                NativeHandle(window),
                new WindowFrameBounds(
                    0,
                    0,
                    Math.Max(1, (int)Math.Ceiling((window.MinWidth + chromeWidth) * scale)),
                    Math.Max(1, (int)Math.Ceiling((window.MinHeight + chromeHeight) * scale))));
            return (bounds.Width, bounds.Height);
        }

        public static void Apply(Window window, WindowFrameBounds bounds)
        {
            bounds = WindowsVisibleFrame.FromVisibleFrameBounds(NativeHandle(window), bounds);
            var center = new PixelPoint(
                bounds.X + bounds.Width / 2,
                bounds.Y + bounds.Height / 2);
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
