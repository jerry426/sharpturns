using System.Diagnostics;
using System.Globalization;
using Avalonia;

namespace SharpTurns.App.Services;

/// <summary>
/// Starts the separate Markdown Viewer app on a project's workspace, from the copy the build places in the app's
/// <c>markdown-viewer</c> folder.
/// </summary>
internal static class MarkdownViewerLauncher
{
    internal const string ViewerDirectoryName = "markdown-viewer";
    internal const string ViewerAssemblyName = "SharpTurns.MarkdownViewer.App";

    /// <summary>
    /// Starts the viewer and returns without waiting for it. <paramref name="screenPoint"/> picks the monitor the
    /// viewer opens on.
    /// </summary>
    public static void Launch(string workspaceDirectory, PixelPoint? screenPoint)
    {
        using var process = Process.Start(CreateStartInfo(AppContext.BaseDirectory, workspaceDirectory, screenPoint))
            ?? throw new InvalidOperationException("The operating system did not start the Markdown Viewer.");
    }

    internal static ProcessStartInfo CreateStartInfo(string appBaseDirectory, string workspaceDirectory, PixelPoint? screenPoint)
    {
        var workspace = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The project workspace does not exist: {workspace}");
        }

        var viewerDirectory = Path.Combine(Path.GetFullPath(appBaseDirectory), ViewerDirectoryName);
        var appHostPath = Path.Combine(viewerDirectory, OperatingSystem.IsWindows() ? $"{ViewerAssemblyName}.exe" : ViewerAssemblyName);

        ProcessStartInfo startInfo;
        if (IsRegularFile(appHostPath))
        {
            startInfo = CreateBaseStartInfo(appHostPath);
        }
        else
        {
            var assemblyPath = Path.Combine(viewerDirectory, $"{ViewerAssemblyName}.dll");
            if (!IsRegularFile(assemblyPath))
            {
                throw new FileNotFoundException("The Markdown Viewer isn't built. Rebuild SharpTurns.", appHostPath);
            }

            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            startInfo = CreateBaseStartInfo(string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost);
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        if (screenPoint is { } point)
        {
            startInfo.ArgumentList.Add("--screen-point");
            startInfo.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"{point.X},{point.Y}"));
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executable) =>
        new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
               && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
               && info.LinkTarget is null;
    }
}
