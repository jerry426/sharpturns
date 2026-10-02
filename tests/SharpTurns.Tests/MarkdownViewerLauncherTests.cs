using Avalonia;
using SharpTurns.App.Services;
using Xunit;

namespace SharpTurns.Tests;

public sealed class MarkdownViewerLauncherTests
{
    [Fact]
    public void StartsTheCopiedViewerOnTheWorkspaceAndScreen()
    {
        var root = Directory.CreateTempSubdirectory("sharpturns-viewer-").FullName;
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace with spaces")).FullName;
            var viewer = Directory.CreateDirectory(Path.Combine(root, MarkdownViewerLauncher.ViewerDirectoryName)).FullName;

            // No build output yet: a clear error, and a missing workspace is reported first.
            Assert.Throws<FileNotFoundException>(() => MarkdownViewerLauncher.CreateStartInfo(root, workspace, null));
            Assert.Throws<DirectoryNotFoundException>(() =>
                MarkdownViewerLauncher.CreateStartInfo(root, Path.Combine(root, "missing"), null));

            // Only the assembly: run it through dotnet.
            var assembly = Path.Combine(viewer, $"{MarkdownViewerLauncher.ViewerAssemblyName}.dll");
            File.WriteAllText(assembly, string.Empty);
            var start = MarkdownViewerLauncher.CreateStartInfo(root, workspace, null);
            Assert.Equal([assembly, "--workspace", workspace], start.ArgumentList);

            // The app host, when the build made one, with the screen to open on.
            var appHost = Path.Combine(viewer, OperatingSystem.IsWindows()
                ? $"{MarkdownViewerLauncher.ViewerAssemblyName}.exe"
                : MarkdownViewerLauncher.ViewerAssemblyName);
            File.WriteAllText(appHost, string.Empty);
            start = MarkdownViewerLauncher.CreateStartInfo(root, workspace, new PixelPoint(-1280, 720));
            Assert.Equal(appHost, start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.Equal(["--workspace", workspace, "--screen-point", "-1280,720"], start.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
