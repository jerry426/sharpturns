using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpTurns.App.Services.Dictation;

internal readonly record struct RecorderStartInfo(ProcessStartInfo ProcessStartInfo, string? SourceDescription);

/// <summary>Process and file helpers shared by the platform command-line recorders.</summary>
internal static class RecorderProcess
{
    private const int SigInt = 2;

    public static string CreateOutputPath() =>
        Path.Combine(Path.GetTempPath(), $"sharpturns-dictation-{Guid.NewGuid():N}.wav");

    public static string? ResolveConfiguredRecorderPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_RECORDER_PATH")?.Trim();
        return !string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath) ? configuredPath : null;
    }

    public static string? ResolveExecutablePath(string executableName, IReadOnlyList<string> fallbackPaths)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fallbackPaths.FirstOrDefault(File.Exists);
    }

    public static ProcessStartInfo CreateBaseStartInfo(string recorderPath)
    {
        return new ProcessStartInfo(recorderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    public static Process Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start audio recorder.");
        }

        return process;
    }

    /// <summary>
    /// Gives the recorder a moment to fail on a bad device or denied microphone access. Kills the recorder and deletes
    /// its output file if it exited or the wait was cancelled.
    /// </summary>
    public static async Task EnsureRecordingStartedAsync(Process process, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                var error = await ReadErrorSnippetAsync(process).ConfigureAwait(false);
                var exitCode = process.ExitCode;
                throw new InvalidOperationException($"Audio recorder exited before recording started (exit code {exitCode}). Check microphone access and the selected input device. {error}".Trim());
            }
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            TryDeleteFile(outputPath);
            throw;
        }
    }

    // Only the recorder this app started, by its own process ID, so it finishes writing the file.
    public static void SendInterrupt(Process process)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            _ = SendSignal(process.Id, SigInt);
        }
    }

    /// <summary>Returns the last 600 characters of the recorder's error output, or empty when it was not redirected.</summary>
    public static async Task<string> ReadErrorSnippetAsync(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            var normalized = stderr.Trim();
            const int maximumLength = 600;
            return normalized.Length <= maximumLength
                ? normalized
                : normalized[^maximumLength..];
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int sig);
}
