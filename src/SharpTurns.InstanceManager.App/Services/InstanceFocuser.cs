using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpTurns.InstanceManager.App.Services;

/// <summary>
/// Brings a running SharpTurns instance's window to the front by targeting its
/// process by PID. On macOS this uses System Events (AppleScript) via osascript;
/// on Windows it activates the process's main window. The instance's PID uniquely
/// identifies it even though every SharpTurns instance shares the same process name.
/// Used when the window can't be linked.
/// </summary>
public sealed class InstanceFocuser
{
    private const int RestoreWindow = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    /// <summary>
    /// Attempts to bring the process with <paramref name="pid"/> to the front.
    /// Returns <c>true</c> when the platform-specific window activation succeeds.
    /// </summary>
    public async Task<bool> FocusAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return FocusWindowsProcess(pid);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }

        // pid is an integer, so embedding it is safe (no injection). The two-arg
        // osascript form keeps the script a single argv element (no shell).
        var script =
            "tell application \"System Events\" to set frontmost of " +
            "(first process whose unix id is " + pid + ") to true";

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            // Process may have exited, or automation permission was denied.
            return false;
        }
    }

    private static bool FocusWindowsProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            var windowHandle = process.MainWindowHandle;
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            ShowWindowAsync(windowHandle, RestoreWindow);
            return SetForegroundWindow(windowHandle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
