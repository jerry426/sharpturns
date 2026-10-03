using System.Runtime.InteropServices;

namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// Thickness of a window's invisible frame border on each side, in physical
/// pixels. Windows gives resizable windows an invisible DWM resize border that
/// sits inside the OS window rectangle but outside the visible frame; macOS
/// and Linux do not, so linked windows only need this compensation on Windows.
/// </summary>
public readonly record struct WindowFrameBorders(int Left, int Top, int Right, int Bottom)
{
    public static WindowFrameBorders Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Converts between a window's OS window rectangle (Avalonia Position +
/// scaled FrameSize) and its visible frame rectangle. Without this mapping,
/// two linked windows whose OS rectangles touch still show a gap on Windows
/// equal to both windows' invisible DWM resize borders. All conversions are
/// the identity mapping off Windows or when the platform handle is unknown,
/// so macOS and Linux behavior is unchanged.
/// </summary>
public static class WindowsVisibleFrame
{
    /// <summary>
    /// Returns the invisible frame borders of the given native window handle,
    /// or false outside Windows or when the geometry cannot be queried.
    /// </summary>
    public static bool TryGetInvisibleBorders(IntPtr windowHandle, out WindowFrameBorders borders)
    {
        borders = WindowFrameBorders.Empty;
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Windows.TryGetInvisibleBorders(windowHandle, out borders);
        }
        catch
        {
            // Window placement is best-effort; never let a DWM query failure
            // disrupt linked-window geometry.
            return false;
        }
    }

    /// <summary>Maps OS window bounds to visible frame bounds.</summary>
    public static WindowFrameBounds ToVisibleFrameBounds(WindowFrameBounds windowBounds, WindowFrameBorders borders)
    {
        ArgumentNullException.ThrowIfNull(windowBounds);
        return new WindowFrameBounds(
            windowBounds.X + borders.Left,
            windowBounds.Y + borders.Top,
            Math.Max(1, windowBounds.Width - borders.Left - borders.Right),
            Math.Max(1, windowBounds.Height - borders.Top - borders.Bottom));
    }

    /// <summary>Maps desired visible frame bounds to the OS window bounds that produce them.</summary>
    public static WindowFrameBounds FromVisibleFrameBounds(WindowFrameBounds visibleBounds, WindowFrameBorders borders)
    {
        ArgumentNullException.ThrowIfNull(visibleBounds);
        return new WindowFrameBounds(
            checked(visibleBounds.X - borders.Left),
            checked(visibleBounds.Y - borders.Top),
            checked(visibleBounds.Width + borders.Left + borders.Right),
            checked(visibleBounds.Height + borders.Top + borders.Bottom));
    }

    /// <summary>Maps the OS window bounds of a native window to its visible frame bounds.</summary>
    public static WindowFrameBounds ToVisibleFrameBounds(IntPtr windowHandle, WindowFrameBounds windowBounds)
    {
        ArgumentNullException.ThrowIfNull(windowBounds);
        return TryGetInvisibleBorders(windowHandle, out var borders)
            ? ToVisibleFrameBounds(windowBounds, borders)
            : windowBounds;
    }

    /// <summary>Maps desired visible frame bounds to the OS window bounds that produce them for a native window.</summary>
    public static WindowFrameBounds FromVisibleFrameBounds(IntPtr windowHandle, WindowFrameBounds visibleBounds)
    {
        ArgumentNullException.ThrowIfNull(visibleBounds);
        return TryGetInvisibleBorders(windowHandle, out var borders)
            ? FromVisibleFrameBounds(visibleBounds, borders)
            : visibleBounds;
    }

    private static class Windows
    {
        private const int DwmExtendedFrameBoundsAttribute = 9; // DWMWA_EXTENDED_FRAME_BOUNDS

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out NativeRectangle value,
            int valueSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

        public static bool TryGetInvisibleBorders(IntPtr windowHandle, out WindowFrameBorders borders)
        {
            borders = WindowFrameBorders.Empty;
            if (!GetWindowRect(windowHandle, out var windowRect)
                || DwmGetWindowAttribute(
                    windowHandle,
                    DwmExtendedFrameBoundsAttribute,
                    out var extendedFrame,
                    Marshal.SizeOf<NativeRectangle>()) != 0
                || extendedFrame.Right <= extendedFrame.Left
                || extendedFrame.Bottom <= extendedFrame.Top)
            {
                return false;
            }

            borders = new WindowFrameBorders(
                extendedFrame.Left - windowRect.Left,
                extendedFrame.Top - windowRect.Top,
                windowRect.Right - extendedFrame.Right,
                windowRect.Bottom - extendedFrame.Bottom);
            return true;
        }
    }
}
