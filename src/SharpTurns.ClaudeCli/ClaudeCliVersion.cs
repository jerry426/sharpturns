using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SharpTurns.ClaudeCli;

/// <summary>The installed CLI's version, as <c>claude --version</c> reports it.</summary>
public static partial class ClaudeCliVersion
{
    /// <summary>
    /// The oldest release the launch profile was verified with: CLI 2.1.286 confirmed CLAUDE.md discovery without
    /// --safe-mode and --restricted, resume, reseeding, and the stream-json flags the client passes.
    /// </summary>
    public static readonly Version Minimum = new(2, 1, 286);

    /// <summary>Reads "2.1.288 (Claude Code)" as 2.1.288; null when the output has no version.</summary>
    public static Version? Parse(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success
            ? new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// Runs <c>--version</c> on the executable (null is claude from the PATH). Throws when it can't be started or
    /// doesn't finish within 10 seconds.
    /// </summary>
    public static async Task<Version?> ReadAsync(string? executable, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable ?? ClaudeCliClient.DefaultExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The process didn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("claude --version didn't finish within 10 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
    }

    [GeneratedRegex(@"\b(\d+)\.(\d+)\.(\d+)\b")]
    private static partial Regex VersionPattern();
}
