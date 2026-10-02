using Avalonia;
using SharpTurns.App.Services;
using Xunit;

namespace SharpTurns.Tests;

public sealed class MarkdownViewerLauncherTests
{
    [Fact]
    public void StartsTheCopiedViewerOnTheWorkspaceAtTheSavedSizeAndScreen()
    {
        var root = Directory.CreateTempSubdirectory("sharpturns-viewer-").FullName;
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace with spaces")).FullName;
            var viewer = Directory.CreateDirectory(Path.Combine(root, MarkdownViewerLauncher.ViewerDirectoryName)).FullName;

            // No build output yet: a clear error, and a missing workspace is reported first.
            var size = new WindowSize(1600, 1000);
            Assert.Throws<FileNotFoundException>(() => MarkdownViewerLauncher.CreateStartInfo(root, workspace, size, null));
            Assert.Throws<DirectoryNotFoundException>(() =>
                MarkdownViewerLauncher.CreateStartInfo(root, Path.Combine(root, "missing"), size, null));

            // Only the assembly: run it through dotnet.
            var assembly = Path.Combine(viewer, $"{MarkdownViewerLauncher.ViewerAssemblyName}.dll");
            File.WriteAllText(assembly, string.Empty);
            var start = MarkdownViewerLauncher.CreateStartInfo(root, workspace, size, null);
            Assert.Equal([assembly, "--workspace", workspace, "--window-width", "1600", "--window-height", "1000"], start.ArgumentList);

            // The app host, when the build made one, with the screen to open on.
            var appHost = Path.Combine(viewer, OperatingSystem.IsWindows()
                ? $"{MarkdownViewerLauncher.ViewerAssemblyName}.exe"
                : MarkdownViewerLauncher.ViewerAssemblyName);
            File.WriteAllText(appHost, string.Empty);
            start = MarkdownViewerLauncher.CreateStartInfo(root, workspace, size, new PixelPoint(-1280, 720));
            Assert.Equal(appHost, start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.Equal(["--workspace", workspace, "--window-width", "1600", "--window-height", "1000", "--screen-point", "-1280,720"],
                start.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
