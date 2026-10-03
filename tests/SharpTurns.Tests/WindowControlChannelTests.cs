using SharpTurns.Core.InstanceManagement;
using Xunit;

namespace SharpTurns.Tests;

public sealed class WindowControlChannelTests
{
    [Fact]
    public async Task DisposeAsync_ReleasesTransportWhenWriterFlushFails()
    {
        var stream = new FailingFlushStream();
        var channel = new WindowControlChannel(stream);
        stream.FailFlush = true;

        await Assert.ThrowsAsync<IOException>(() => channel.DisposeAsync().AsTask());

        Assert.True(stream.IsDisposed);
        await channel.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedPipe_CanReconnectAfterPeerDisconnects(bool writeAfterDisconnect)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var endpoint = new WindowControlLocalIpcEndpoint(
            WindowControlLocalIpcKind.NamedPipe, $"sharpturns-window-control-test-{Guid.NewGuid():N}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var listener = WindowControlLocalIpcListener.Bind(endpoint);
        var accept = listener.AcceptAsync(timeout.Token).AsTask();
        var client = await WindowControlLocalIpcClient.ConnectWithRetryAsync(endpoint, timeout.Token);
        await using var server = await accept;
        var handle = ((System.IO.Pipes.NamedPipeServerStream)server).SafePipeHandle;
        var channel = new WindowControlChannel(server);
        await client.DisposeAsync();
        Assert.Null(await channel.ReadAsync(timeout.Token));
        if (writeAfterDisconnect)
        {
            await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(
                new WindowControlMessage(WindowControlMessageTypes.Pong), timeout.Token).AsTask());
        }
        try
        {
            await channel.DisposeAsync();
        }
        catch (IOException)
        {
            // Flushing a disconnected pipe may fail, but must still close it.
        }

        Assert.True(handle.IsClosed);
        var reconnect = listener.AcceptAsync(timeout.Token).AsTask();
        await using var nextClient = await WindowControlLocalIpcClient.ConnectWithRetryAsync(endpoint, timeout.Token);
        await using var nextServer = await reconnect;
        Assert.True(nextServer.CanRead);
    }

    private sealed class FailingFlushStream : MemoryStream
    {
        public bool FailFlush { get; set; }
        public bool IsDisposed { get; private set; }

        public override void Flush()
        {
            if (FailFlush)
                throw new IOException("The peer disconnected.");
            base.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            FailFlush ? Task.FromException(new IOException("The peer disconnected.")) : base.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
