using System.Buffers.Binary;
using System.Text.Json;

namespace SharpTurns.Core.InstanceManagement;

public sealed record InstanceManagerMessage(
    string Type,
    AppInstanceRecord? Instance = null,
    int ProtocolVersion = 1);

/// <summary>Length-prefixed, size-bounded messages over the existing same-user local transport.</summary>
public static class InstanceManagerProtocol
{
    public const int MaximumMessageBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, InstanceManagerMessage message, CancellationToken cancellationToken)
    {
        Validate(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Instance-manager message is too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<InstanceManagerMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("Invalid instance-manager message length.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<InstanceManagerMessage>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Empty instance-manager message.");
        Validate(message);
        return message;
    }

    private static void Validate(InstanceManagerMessage message)
    {
        if (message.ProtocolVersion != 1 || message.Type is not ("report" or "focus" or "ack"))
        {
            throw new InvalidDataException("Unsupported instance-manager protocol or message type.");
        }

        if (message.Type != "report")
        {
            if (message.Instance is not null)
                throw new InvalidDataException("Unexpected instance snapshot.");
            return;
        }

        var instance = message.Instance;
        if (instance is null || !Guid.TryParseExact(instance.Id, "N", out _)
            || instance.Pid <= 0
            || instance.Status is not ("running" or "stopped")
            || instance.ProjectTitle is { Length: > 4096 }
            || instance.ConversationTitle is { Length: > 4096 }
            || instance.ConversationModel is { Length: > 1024 }
            || instance.ProjectColor is { Length: > 32 })
        {
            throw new InvalidDataException("Invalid instance snapshot.");
        }
    }
}

public sealed class InstanceManagerClient
{
    private readonly WindowControlLocalIpcEndpoint _endpoint;

    public InstanceManagerClient(WindowControlLocalIpcEndpoint? endpoint = null) =>
        _endpoint = endpoint ?? WindowControlLocalIpcEndpoint.ForInstanceManager();

    public Task ReportAsync(AppInstanceRecord instance, CancellationToken cancellationToken = default) =>
        SendAsync(new("report", instance), cancellationToken);

    public Task FocusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new("focus"), cancellationToken);

    private async Task SendAsync(InstanceManagerMessage message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var stream = await WindowControlLocalIpcClient
                .ConnectWithRetryAsync(_endpoint, deadline.Token).ConfigureAwait(false);
            await InstanceManagerProtocol.WriteAsync(stream, message, deadline.Token).ConfigureAwait(false);
            var response = await InstanceManagerProtocol.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
            if (response.Type != "ack")
                throw new InvalidDataException("Instance manager did not acknowledge the message.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Instance manager did not respond before the deadline.", ex);
        }
    }
}
