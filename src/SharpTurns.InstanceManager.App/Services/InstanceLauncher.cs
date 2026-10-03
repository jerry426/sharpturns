using System.Diagnostics;
using System.Text.Json;
using SharpTurns.Core.InstanceManagement;

namespace SharpTurns.InstanceManager.App.Services;

/// <summary>
/// Launches SharpTurns instances from the repository the manager was built in, with <c>dotnet run</c>. The child
/// survives the manager exiting. On Windows it runs the existing build, since running instances lock its files;
/// elsewhere it builds first, so Reload picks up source changes.
/// </summary>
public sealed class InstanceLauncher
{
    private const string AppProject = "src/SharpTurns.App/SharpTurns.App.csproj";
    private readonly string? _repositoryRoot;

    public InstanceLauncher()
        : this(FindRepositoryRoot(AppContext.BaseDirectory))
    {
    }

    public InstanceLauncher(string? repositoryRoot) => _repositoryRoot = repositoryRoot;

    public bool HasLaunchTarget => _repositoryRoot is not null;

    public Task LaunchAsync(string instanceId, Task registered, CancellationToken cancellationToken = default) =>
        LaunchAsync(instanceId, registered, startupSession: null, cancellationToken);

    /// <summary>Starts a replacement that reopens the same project and conversation.</summary>
    public Task ReloadAsync(long? projectId, long? conversationId, string instanceId, Task registered,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(instanceId, registered, new AppStartupSession(projectId, conversationId), cancellationToken);

    private async Task LaunchAsync(string instanceId, Task registered, AppStartupSession? startupSession,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(registered);
        cancellationToken.ThrowIfCancellationRequested();
        if (_repositoryRoot is null)
            throw new InvalidOperationException("The SharpTurns repository wasn't found above the Instance Manager's folder.");

        var path = ResolveAugmentedPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = FindDotnet(path),
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_repositoryRoot, AppProject));
        if (OperatingSystem.IsWindows()) startInfo.ArgumentList.Add("--no-build");

        // Child SharpTurns processes, and the claude and recorder processes they start, need tools a GUI-launched
        // manager's PATH may leave out.
        startInfo.Environment["PATH"] = path;
        startInfo.Environment[AppInstanceLaunchEnvironment.InstanceIdVariable] = instanceId;
        startInfo.Environment.Remove(AppInstanceLaunchEnvironment.StartupSessionVariable);
        if (startupSession is not null)
            startInfo.Environment[AppInstanceLaunchEnvironment.StartupSessionVariable] = JsonSerializer.Serialize(startupSession);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start dotnet.");

        await WaitForRegistrationAsync(process, registered, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        // Disposing the handle does not terminate the child. Do not redirect output: the child must survive the manager
        // exiting.
    }

    internal static async Task WaitForRegistrationAsync(
        Process process,
        Task registered,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = process.WaitForExitAsync(waitCts.Token);
        try
        {
            await Task.WhenAny(registered, exited).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (registered.IsCompleted)
            {
                await registered.ConfigureAwait(false);
                return;
            }

            await exited.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"dotnet run exited with code {process.ExitCode} before SharpTurns registered. Check that the app builds" +
                (OperatingSystem.IsWindows() ? "; Windows launches use the existing build, so build it first." : "."));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "SharpTurns did not register within two minutes. dotnet run may still be running; check it before retrying.");
        }
        finally
        {
            // Stop observing, not the detached child, on registration or manager shutdown.
            await waitCts.CancelAsync().ConfigureAwait(false);
            try { await exited.ConfigureAwait(false); }
            catch (OperationCanceledException) when (waitCts.IsCancellationRequested) { }
        }
    }

    /// <summary>The nearest folder at or above <paramref name="start"/> holding SharpTurns.slnx and the app project.</summary>
    internal static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpTurns.slnx"))
                && File.Exists(Path.Combine(directory.FullName, AppProject)))
                return directory.FullName;
        }
        return null;
    }

    // macOS apps launched from the Dock or Finder get a minimal PATH and don't read the shell profile, so Homebrew
    // and the .NET install are added. The existing PATH is kept; these only go in front.
    private static string ResolveAugmentedPath()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!OperatingSystem.IsMacOS())
            return currentPath;

        var currentDirs = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var extraDirs = new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/local/share/dotnet" }
            .Where(dir => Directory.Exists(dir) && !currentDirs.Contains(dir))
            .ToArray();
        if (extraDirs.Length == 0)
            return currentPath;
        var augmented = string.Join(Path.PathSeparator, extraDirs);
        return currentPath.Length == 0 ? augmented : augmented + Path.PathSeparator + currentPath;
    }

    // Process.Start searches the manager's own PATH, so look through the child's.
    private static string FindDotnet(string path)
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return name;
    }
}
