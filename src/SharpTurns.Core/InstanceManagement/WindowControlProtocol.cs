using System.Text;
using System.Text.Json;

namespace SharpTurns.Core.InstanceManagement;

public static class WindowControlMessageTypes
{
    public const string Ready = "ready";
    public const string Attach = "attach";
    public const string SetBounds = "set_bounds";
    public const string Detach = "detach";
    public const string Activate = "activate";
    public const string Raise = "raise";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Attached = "attached";
    public const string Detached = "detached";
    public const string GeometryChanged = "geometry_changed";
    public const string WindowActivated = "window_activated";
    public const string Error = "error";
}

public sealed record WindowControlMessage(
    string Type,
    int ProtocolVersion = WindowControlProtocol.ProtocolVersion,
    string? SessionId = null,
    long Sequence = 0,
    WindowFrameBounds? Bounds = null,
    int? Pid = null,
    int? MinimumWidth = null,
    int? MinimumHeight = null,
    string? Error = null);

/// <summary>
/// Bounded newline-delimited JSON channel used by the local window-control
/// connection. The surrounding transport is restricted to the current user.
/// </summary>
public sealed class WindowControlChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public WindowControlChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _reader = new StreamReader(stream, WindowControlProtocol.Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, WindowControlProtocol.Utf8, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public async ValueTask SendAsync(
        WindowControlMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        WindowControlProtocol.Validate(message);
        var json = JsonSerializer.Serialize(message, WindowControlProtocol.JsonOptions);
        if (WindowControlProtocol.Utf8.GetByteCount(json) > WindowControlProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Window-control message exceeds the bounded message size.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<WindowControlMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        if (WindowControlProtocol.Utf8.GetByteCount(line) > WindowControlProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Window-control message exceeds the bounded message size.");
        }

        var message = JsonSerializer.Deserialize<WindowControlMessage>(line, WindowControlProtocol.JsonOptions)
            ?? throw new InvalidDataException("Window-control message was empty.");
        WindowControlProtocol.Validate(message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Flushing the writer can fail after a peer disconnects. Always close
            // the transport so a Windows pipe's single server slot is released.
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Dispose();
            }
        }
    }
}

public static class WindowControlProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 8 * 1024;

    internal static UTF8Encoding Utf8 { get; } = new(encoderShouldEmitUTF8Identifier: false);
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static void Validate(WindowControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ProtocolVersion != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported window-control protocol version {message.ProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 40)
        {
            throw new InvalidDataException("Window-control message type is invalid.");
        }

        if (message.SessionId is { Length: > 64 })
        {
            throw new InvalidDataException("Window-control session identifier is too long.");
        }

        if (message.Bounds is { IsValid: false })
        {
            throw new InvalidDataException("Window-control bounds must have positive width and height.");
        }

        if (message.Error is { Length: > 512 })
        {
            throw new InvalidDataException("Window-control error text is too long.");
        }
    }
}
