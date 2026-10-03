using System.ComponentModel;
using SharpTurns.Core.InstanceManagement;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ProcessCpuSamplerTests
{
    [Fact]
    public void UsesElapsedTimeAndAllowsMultipleCores()
    {
        var clock = new SampleClock();
        var cpu = TimeSpan.Zero;
        var sampler = new ProcessCpuSampler(() => (DateTime.UnixEpoch, cpu), clock);
        Assert.Null(sampler.Sample());

        clock.Advance(2);
        cpu = TimeSpan.FromSeconds(0.008);
        Assert.Equal(0.4, sampler.Sample()!.Value, precision: 6);

        clock.Advance(3); // Actual elapsed time, not the nominal two-second interval.
        cpu += TimeSpan.FromSeconds(4.5);
        Assert.Equal(150, sampler.Sample()!.Value, precision: 6);

        clock.Advance(2);
        Assert.Equal(0, sampler.Sample());
    }

    [Fact]
    public void ReusedPidRemainsUnavailable()
    {
        var clock = new SampleClock();
        var started = DateTime.UnixEpoch;
        var sampler = new ProcessCpuSampler(() => (started, TimeSpan.Zero), clock);
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        started = started.AddMinutes(1);
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        Assert.Null(sampler.Sample());
    }

    [Fact]
    public void FailedReadClearsBaselineBeforeRecovery()
    {
        var clock = new SampleClock();
        var fail = false;
        var sampler = new ProcessCpuSampler(
            () => fail ? throw new Win32Exception() : (DateTime.UnixEpoch, TimeSpan.Zero), clock);
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        fail = true;
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        fail = false;
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        Assert.Equal(0, sampler.Sample());
    }

    [Fact]
    public void InvalidDeltasDoNotProduceNegativeOrInfiniteUsage()
    {
        var clock = new SampleClock();
        var cpu = TimeSpan.FromSeconds(1);
        var sampler = new ProcessCpuSampler(() => (DateTime.UnixEpoch, cpu), clock);
        Assert.Null(sampler.Sample());
        Assert.Null(sampler.Sample()); // No elapsed time.
        clock.Advance(2);
        cpu = TimeSpan.Zero;
        Assert.Null(sampler.Sample());
        clock.Advance(2);
        Assert.Equal(0, sampler.Sample());
    }

    [Fact]
    public async Task CanReadLocalProcessAndHandlesMissingProcess()
    {
        var sampler = new ProcessCpuSampler(Environment.ProcessId);
        Assert.Null(sampler.Sample());
        await Task.Delay(50);
        var usage = sampler.Sample();
        Assert.NotNull(usage);
        Assert.True(double.IsFinite(usage.Value) && usage.Value >= 0);
        Assert.Null(new ProcessCpuSampler(int.MaxValue).Sample());
    }

    private sealed class SampleClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
