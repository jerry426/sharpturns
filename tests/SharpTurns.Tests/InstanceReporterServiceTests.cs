using System.Threading.Channels;
using SharpTurns.App.Services;
using SharpTurns.Core.InstanceManagement;
using Xunit;

namespace SharpTurns.Tests;

public sealed class InstanceReporterServiceTests
{
    private static readonly InstanceSnapshot Snapshot = new(null, null, null, 915, "Instance Manager", "claude-opus-5-5", false);

    [Fact]
    public async Task StartAndStop_ReportAssignedIdentityAndDoNotCaptureUiOnShutdown()
    {
        var reports = Channel.CreateUnbounded<AppInstanceRecord>();
        var instanceId = Guid.NewGuid().ToString("N");
        var captures = 0;
        await using var reporter = new InstanceReporterService(
            (record, _) => { reports.Writer.TryWrite(record); return Task.CompletedTask; },
            123, _ => { Interlocked.Increment(ref captures); return Task.FromResult(Snapshot); },
            heartbeatInterval: TimeSpan.FromHours(1), instanceId: instanceId);
        await reporter.StartAsync();
        var record = await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(instanceId, record.Id);
        Assert.Equal("claude-opus-5-5", record.ConversationModel);
        Assert.Equal("running", record.Status);

        await reporter.StopAsync();
        var stopped = await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal(record.Id, stopped.Id);
        Assert.Equal(1, captures);
    }


    [Fact]
    public async Task Emit_ReportsProjectColorChanges()
    {
        var reports = Channel.CreateUnbounded<AppInstanceRecord>();
        var snapshot = Snapshot with { ProjectColor = "#123456" };
        await using var reporter = new InstanceReporterService(
            (record, _) => { reports.Writer.TryWrite(record); return Task.CompletedTask; },
            123, _ => Task.FromResult(Volatile.Read(ref snapshot)),
            heartbeatInterval: TimeSpan.FromHours(1));
        await reporter.StartAsync();
        Assert.Equal("#123456", (await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ProjectColor);
        Volatile.Write(ref snapshot, Snapshot with { ProjectColor = "#ABCDEF" });
        reporter.Emit();
        Assert.Equal("#ABCDEF", (await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ProjectColor);
    }

    [Fact]
    public async Task Emit_CoalescesUpdatesAndStopCancelsInFlightReport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        var runningReports = 0;
        var stoppedReports = 0;
        await using var reporter = new InstanceReporterService(async (record, token) =>
        {
            if (record.Status == "stopped") { stoppedReports++; return; }
            Interlocked.Increment(ref runningReports);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled = true; throw; }
        }, 123, _ => Task.FromResult(Snapshot),
            heartbeatInterval: TimeSpan.FromHours(1));
        await reporter.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) reporter.Emit();
        Assert.Equal(1, runningReports);
        await reporter.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(canceled);
        Assert.Equal(1, stoppedReports);
    }

    [Fact]
    public async Task FailedReport_IsRetriedWithLatestState()
    {
        var attempts = 0;
        var delivered = new TaskCompletionSource<AppInstanceRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reporter = new InstanceReporterService((record, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Manager absent");
            delivered.TrySetResult(record);
            return Task.CompletedTask;
        }, 123, _ => Task.FromResult(Snapshot with { TurnActive = true }),
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        await reporter.StartAsync();
        var record = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(record.TurnActive);
        Assert.True(attempts >= 2);
    }
}
