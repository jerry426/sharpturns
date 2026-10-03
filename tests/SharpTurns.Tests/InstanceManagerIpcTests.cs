using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using SharpTurns.App.Services;
using SharpTurns.Core;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.InstanceManager.App.ViewModels;
using Xunit;

namespace SharpTurns.Tests;

public sealed class InstanceManagerIpcTests
{
    [Fact]
    public void CardReloadTracksSelectionAndDisablesProcessActionsWhileBusy()
    {
        var record = CreateRecord();
        InstanceCardViewModel? reloaded = null;
        var card = new InstanceCardViewModel(record, _ => { }, reloadAction: value => reloaded = value);
        var notifications = 0;
        card.ReloadCommand.CanExecuteChanged += (_, _) => notifications++;
        Assert.True(card.ReloadCommand.CanExecute(null));
        card.ReloadCommand.Execute(null);
        Assert.Same(card, reloaded);

        card.UpdateFrom(record with { TurnActive = true, ProjectId = 12, ConversationId = 34 });
        Assert.Equal(12, card.ProjectId);
        Assert.Equal(34, card.ConversationId);
        Assert.False(card.ReloadCommand.CanExecute(null));
        Assert.False(card.StopCommand.CanExecute(null));
        card.UpdateFrom(record);
        card.IsProcessActionPending = true;
        Assert.False(card.ReloadCommand.CanExecute(null));
        Assert.False(card.StopCommand.CanExecute(null));
        card.IsProcessActionPending = false;
        Assert.True(card.ReloadCommand.CanExecute(null));
        Assert.True(card.StopCommand.CanExecute(null));
        Assert.Equal(4, notifications);
        Assert.False(new InstanceCardViewModel(record).ReloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReportsOnlyVisibleChanges_AndHandlesFocusAndStop()
    {
        var endpoint = CreateEndpoint();
        await using var server = InstanceManagerServer.TryCreate(endpoint)!;
        Assert.NotNull(server);
        Assert.Null(InstanceManagerServer.TryCreate(endpoint));
        var changes = Channel.CreateUnbounded<AppInstanceRecord>();
        var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.InstanceChanged += record => changes.Writer.TryWrite(record);
        server.FocusRequested += () => focused.TrySetResult();
        server.Start();
        var client = new InstanceManagerClient(endpoint);
        var record = CreateRecord();
        await client.ReportAsync(record);
        Assert.Equal(record.Id, (await NextAsync(changes)).Id);
        await client.ReportAsync(record with { HeartbeatAt = DateTimeOffset.UtcNow.AddHours(1), TimestampUpdated = DateTimeOffset.UtcNow });
        Assert.False(changes.Reader.TryRead(out _));
        await client.ReportAsync(record with { TurnActive = true, ConversationModel = "New model" });
        var updated = await NextAsync(changes);
        Assert.True(updated.TurnActive);
        Assert.Equal("New model", updated.ConversationModel);
        await client.FocusAsync();
        await focused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.ReportAsync(record with { Status = "stopped" });
        Assert.Equal("stopped", (await NextAsync(changes)).Status);
        await client.ReportAsync(record with { Status = "stopped" });
        Assert.False(changes.Reader.TryRead(out _));
    }


    [Fact]
    public async Task ProjectColorChangesReachCardWithoutOtherStateChanges()
    {
        var endpoint = CreateEndpoint();
        await using var server = InstanceManagerServer.TryCreate(endpoint)!;
        var changes = Channel.CreateUnbounded<AppInstanceRecord>();
        server.InstanceChanged += record => changes.Writer.TryWrite(record);
        server.Start();
        var client = new InstanceManagerClient(endpoint);
        var record = CreateRecord();
        await client.ReportAsync(record);
        var card = new InstanceCardViewModel(await NextAsync(changes));
        Assert.Equal(ProjectColor.Default, card.ProjectBorderColor);
        var notified = new List<string?>();
        card.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        await client.ReportAsync(record with { ProjectColor = "#abcdef" });
        card.UpdateFrom(await NextAsync(changes));
        Assert.Equal("#ABCDEF", card.ProjectBorderColor);
        Assert.Contains(nameof(card.ProjectBorderColor), notified);
        card.IsSelected = true;
        Assert.Equal("#ABCDEF", card.ProjectBorderColor);
        await client.ReportAsync(record with { ProjectColor = "invalid" });
        card.UpdateFrom(await NextAsync(changes));
        Assert.Equal(ProjectColor.Default, card.ProjectBorderColor);
    }

    [Fact]
    public async Task ReporterDiscoversLateManager_AndRebuildsRegistryAfterRestart()
    {
        var endpoint = CreateEndpoint();
        var client = new InstanceManagerClient(endpoint);
        var attempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reporter = new InstanceReporterService(async (record, token) =>
        {
            attempts.TrySetResult();
            await client.ReportAsync(record, token);
        }, Environment.ProcessId,
            _ => Task.FromResult(new InstanceSnapshot(1, "Project", null, 2, "Conversation", "Model", true)),
            heartbeatInterval: TimeSpan.FromMilliseconds(100));
        await reporter.StartAsync();
        await attempts.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string id;
        await using (var first = InstanceManagerServer.TryCreate(endpoint)!)
        {
            var changes = Channel.CreateUnbounded<AppInstanceRecord>();
            first.InstanceChanged += record => changes.Writer.TryWrite(record);
            first.Start();
            var registered = await NextAsync(changes);
            Assert.True(registered.TurnActive);
            id = registered.Id;
        }
        await using var second = InstanceManagerServer.TryCreate(endpoint)!;
        Assert.NotNull(second);
        var restarted = Channel.CreateUnbounded<AppInstanceRecord>();
        second.InstanceChanged += record => restarted.Writer.TryWrite(record);
        second.Start();
        Assert.Equal(id, (await NextAsync(restarted)).Id);
        await reporter.StopAsync();
        Assert.Equal("stopped", (await NextAsync(restarted)).Status);
    }

    [Fact]
    public async Task StaleReportsExpireByLocalMonotonicTime_NotRemoteTimestamp()
    {
        var clock = new ManualTimeProvider();
        var changes = Channel.CreateUnbounded<AppInstanceRecord>();
        var endpoint = CreateEndpoint();
        await using var active = InstanceManagerServer.TryCreate(endpoint, clock)!;
        active.InstanceChanged += record => changes.Writer.TryWrite(record);
        active.Start();
        var client = new InstanceManagerClient(endpoint);
        await client.ReportAsync(CreateRecord() with { HeartbeatAt = DateTimeOffset.UtcNow.AddYears(1) });
        Assert.Equal("running", (await NextAsync(changes)).Status);
        clock.Advance(TimeSpan.FromSeconds(21));
        var expired = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("stopped", expired.Status);
    }

    [Fact]
    public async Task InvalidClientDoesNotBreakListener_AndUnixEndpointIsPrivate()
    {
        var endpoint = CreateEndpoint();
        await using var server = InstanceManagerServer.TryCreate(endpoint)!;
        server.Start();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(endpoint.Address));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(endpoint.Address)!));
        }
        await using (var stream = await WindowControlLocalIpcClient.ConnectWithRetryAsync(endpoint))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, InstanceManagerProtocol.MaximumMessageBytes + 1);
            await stream.WriteAsync(header);
        }
        await new InstanceManagerClient(endpoint).FocusAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32769)]
    public async Task ProtocolRejectsOversizeBeforeReadingBody(int size)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceManagerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentInstancesCanReport_AndStalledClientTimesOut()
    {
        var endpoint = CreateEndpoint();
        await using var server = InstanceManagerServer.TryCreate(endpoint)!;
        var changes = Channel.CreateUnbounded<AppInstanceRecord>();
        server.InstanceChanged += record => changes.Writer.TryWrite(record);
        server.Start();
        var records = Enumerable.Range(0, 6).Select(_ => CreateRecord()).ToArray();
        await Task.WhenAll(records.Select(record => new InstanceManagerClient(endpoint).ReportAsync(record)));
        var ids = new HashSet<string>();
        foreach (var _ in records)
            ids.Add((await NextAsync(changes)).Id);
        Assert.Equal(records.Length, ids.Count);

        await using var stalled = await WindowControlLocalIpcClient.ConnectWithRetryAsync(endpoint);
        // Send a partial header and hold the connection open. The server must
        // bound this read so another client can still use the endpoint.
        await stalled.WriteAsync(new byte[] { 1 });
        await new InstanceManagerClient(endpoint).FocusAsync();
    }

    [Theory]
    [InlineData("{\"type\":\"focus\",\"protocolVersion\":2}")]
    [InlineData("{\"type\":\"unknown\",\"protocolVersion\":1}")]
    [InlineData("{\"type\":\"report\",\"protocolVersion\":1}")]
    public async Task ProtocolRejectsUnsupportedOrInvalidMessages(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceManagerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    private static Task<AppInstanceRecord> NextAsync(Channel<AppInstanceRecord> changes) =>
        changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));

    private static AppInstanceRecord CreateRecord() => new(
        Guid.NewGuid().ToString("N"), Environment.ProcessId, 1, "Project", 2,
        "Conversation", "Model", false, "running", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static WindowControlLocalIpcEndpoint CreateEndpoint()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var production = WindowControlLocalIpcEndpoint.ForInstanceManager();
        return production.Kind == WindowControlLocalIpcKind.NamedPipe
            ? new(production.Kind, $"sharpturns-manager-test-{suffix}")
            : new(production.Kind, Path.Combine(Path.GetDirectoryName(production.Address)!, $"test-{suffix}.sock"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
