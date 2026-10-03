using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SharpTurns.Core.InstanceManagement;

public enum WindowControlLocalIpcKind
{
    UnixDomainSocket,
    NamedPipe,
}

public sealed record WindowControlLocalIpcEndpoint(
    WindowControlLocalIpcKind Kind,
    string Address)
{
    private const int MaximumUnixSocketPathBytes = 100;

    public static WindowControlLocalIpcEndpoint ForInstanceManager()
    {
        var userHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12].ToLowerInvariant();
        if (OperatingSystem.IsWindows())
            return new(WindowControlLocalIpcKind.NamedPipe, $"sharpturns-instance-manager-{userHash}");

        var path = Path.Combine(Path.GetTempPath(), $"sharpturns-wc-{userHash}", "manager.sock");
        if (Encoding.UTF8.GetByteCount(path) > MaximumUnixSocketPathBytes)
            path = Path.Combine("/tmp", $"sharpturns-wc-{userHash}", "manager.sock");
        return new(WindowControlLocalIpcKind.UnixDomainSocket, path);
    }

    public static WindowControlLocalIpcEndpoint ForProcess(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (OperatingSystem.IsWindows())
        {
            return new WindowControlLocalIpcEndpoint(
                WindowControlLocalIpcKind.NamedPipe,
                $"sharpturns-window-control-{pid}");
        }

        var userHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12].ToLowerInvariant();
        var directory = Path.Combine(Path.GetTempPath(), $"sharpturns-wc-{userHash}");
        var path = Path.Combine(directory, $"{pid}.sock");
        if (Encoding.UTF8.GetByteCount(path) > MaximumUnixSocketPathBytes)
        {
            directory = Path.Combine("/tmp", $"sharpturns-wc-{userHash}");
            path = Path.Combine(directory, $"{pid}.sock");
        }

        if (Encoding.UTF8.GetByteCount(path) > MaximumUnixSocketPathBytes)
        {
            throw new PlatformNotSupportedException("The local window-control socket path exceeds the platform-safe limit.");
        }

        return new WindowControlLocalIpcEndpoint(WindowControlLocalIpcKind.UnixDomainSocket, path);
    }
}

public sealed class WindowControlLocalIpcListener : IAsyncDisposable
{
    private static readonly UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Socket? _unixListener;
    private readonly Mutex? _pipeOwnership;
    private int _disposed;

    private WindowControlLocalIpcListener(
        WindowControlLocalIpcEndpoint endpoint,
        Socket? unixListener,
        Mutex? pipeOwnership)
    {
        Endpoint = endpoint;
        _unixListener = unixListener;
        _pipeOwnership = pipeOwnership;
    }

    public WindowControlLocalIpcEndpoint Endpoint { get; }

    public static WindowControlLocalIpcListener Bind(WindowControlLocalIpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Kind switch
        {
            WindowControlLocalIpcKind.UnixDomainSocket => BindUnix(endpoint),
            WindowControlLocalIpcKind.NamedPipe => BindNamedPipe(endpoint),
            _ => throw new PlatformNotSupportedException("Unsupported local window-control transport."),
        };
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_unixListener is not null)
        {
            var socket = await _unixListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }

        var pipe = new NamedPipeServerStream(
            Endpoint.Address,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _unixListener?.Dispose();
        if (_unixListener is not null && File.Exists(Endpoint.Address))
        {
            File.Delete(Endpoint.Address);
        }

        _pipeOwnership?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WindowControlLocalIpcListener BindUnix(WindowControlLocalIpcEndpoint endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix-domain sockets are not used on Windows.");
        }

        var directory = Path.GetDirectoryName(endpoint.Address)
            ?? throw new ArgumentException("Unix socket path requires a parent directory.", nameof(endpoint));
        if (Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || File.GetUnixFileMode(directory) != DirectoryMode)
            {
                throw new UnauthorizedAccessException("Window-control runtime directory must be a real owner-only 0700 directory.");
            }
        }
        else
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, DirectoryMode);
        }

        if (File.Exists(endpoint.Address))
        {
            if ((File.GetAttributes(endpoint.Address) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Window-control socket path must not be a symbolic link.");
            }

            File.Delete(endpoint.Address);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var ownsPath = false;
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            ownsPath = true;
            socket.Listen(backlog: 1);
            File.SetUnixFileMode(endpoint.Address, SocketMode);
            return new WindowControlLocalIpcListener(endpoint, socket, pipeOwnership: null);
        }
        catch
        {
            socket.Dispose();
            if (ownsPath && File.Exists(endpoint.Address))
            {
                File.Delete(endpoint.Address);
            }

            throw;
        }
    }

    private static WindowControlLocalIpcListener BindNamedPipe(WindowControlLocalIpcEndpoint endpoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pipes are used only on Windows.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Address)))[..24];
        var mutex = new Mutex(initiallyOwned: false, $"Local\\SharpTurns.WindowControl.{hash}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            throw new IOException("Window-control named-pipe endpoint is already owned.");
        }

        return new WindowControlLocalIpcListener(endpoint, unixListener: null, mutex);
    }
}

public static class WindowControlLocalIpcClient
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
    ];

    public static async ValueTask<Stream> ConnectWithRetryAsync(
        WindowControlLocalIpcEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Exception? lastError = null;
        for (var attempt = 0; attempt <= ReconnectDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ConnectOnceAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
            {
                lastError = ex;
                if (attempt == ReconnectDelays.Length)
                {
                    break;
                }
            }

            await Task.Delay(ReconnectDelays[attempt], cancellationToken).ConfigureAwait(false);
        }

        throw new IOException("SharpTurns window-control endpoint was unavailable.", lastError);
    }

    private static async ValueTask<Stream> ConnectOnceAsync(
        WindowControlLocalIpcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Kind == WindowControlLocalIpcKind.UnixDomainSocket)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Unix-domain sockets are not used on Windows.");
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pipes are used only on Windows.");
        }

        var pipe = new NamedPipeClientStream(
            ".",
            endpoint.Address,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout: 750, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
