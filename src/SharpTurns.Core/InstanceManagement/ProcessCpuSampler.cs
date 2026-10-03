using System.ComponentModel;
using System.Diagnostics;

namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// Samples one process, not its children. 100% represents one logical core.
/// Owned by a single sampling loop; no process handles are retained between reads.
/// </summary>
public sealed class ProcessCpuSampler
{
    private readonly Func<(DateTime Started, TimeSpan Cpu)> _read;
    private readonly TimeProvider _timeProvider;
    private DateTime? _started;
    private (TimeSpan Cpu, long Timestamp)? _previous;

    public ProcessCpuSampler(int pid)
        : this(() => ReadProcess(pid), TimeProvider.System) { }

    internal ProcessCpuSampler(Func<(DateTime Started, TimeSpan Cpu)> read, TimeProvider timeProvider)
    {
        _read = read;
        _timeProvider = timeProvider;
    }

    public double? Sample()
    {
        try
        {
            var (started, cpu) = _read();
            var timestamp = _timeProvider.GetTimestamp();
            _started ??= started;
            if (_started != started)
            {
                // The OS reused this PID. Do not display another process's CPU
                // against the old instance card, even on subsequent samples.
                _previous = null;
                return null;
            }

            var previous = _previous;
            _previous = (cpu, timestamp);
            if (previous is not { } baseline)
                return null;
            var elapsed = _timeProvider.GetElapsedTime(baseline.Timestamp, timestamp);
            var consumed = cpu - baseline.Cpu;
            return elapsed > TimeSpan.Zero && consumed >= TimeSpan.Zero
                ? consumed.TotalSeconds / elapsed.TotalSeconds * 100
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            // Exit/access races are normal; require a fresh baseline on recovery.
            _previous = null;
            return null;
        }
    }

    private static (DateTime Started, TimeSpan Cpu) ReadProcess(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return (process.StartTime.ToUniversalTime(), process.TotalProcessorTime);
    }
}
