using System.Diagnostics;

namespace SharpTurns.App.Services;

/// <summary>Opens a conversation's workspace folder in Visual Studio Code.</summary>
internal static class VsCodeLauncher
{
    internal const string MacBundleId = "com.microsoft.VSCode";

    public static void Launch(string folder)
    {
        using var process = Process.Start(CreateStartInfo(folder))
            ?? throw new InvalidOperationException("The operating system did not start VS Code.");
    }

    internal static ProcessStartInfo CreateStartInfo(string folder)
    {
        var workspace = Path.GetFullPath(folder);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The workspace does not exist: {workspace}");
        }

        ProcessStartInfo startInfo;
        if (OperatingSystem.IsMacOS())
        {
            // A Finder/Dock-launched app has a minimal PATH, so resolve VS Code by bundle id instead of the `code` CLI.
            startInfo = new ProcessStartInfo("/usr/bin/open");
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add(MacBundleId);
        }
        else if (OperatingSystem.IsWindows())
        {
            // Start Code.exe directly rather than the code.cmd batch wrapper on PATH.
            startInfo = new ProcessStartInfo(FindWindowsExecutable()
                ?? throw new FileNotFoundException("VS Code is not installed in the default per-user or system location."));
        }
        else
        {
            startInfo = new ProcessStartInfo("code");
        }

        startInfo.ArgumentList.Add(workspace);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return startInfo;
    }

    private static string? FindWindowsExecutable()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
