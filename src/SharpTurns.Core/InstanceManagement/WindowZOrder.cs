using System.Runtime.InteropServices;

namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// Raises a native top-level window without transferring keyboard focus from
/// the window the user actually activated when the platform provides a
/// reliable native operation for doing so.
/// </summary>
public static class WindowZOrder
{
    public static bool TryRaiseWithoutActivation(IntPtr handle, string? handleDescriptor)
    {
        if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(handleDescriptor))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsMacOS()
                && (string.Equals(handleDescriptor, "NSVIEW", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(handleDescriptor, "NSWINDOW", StringComparison.OrdinalIgnoreCase)))
            {
                return MacOS.Raise(handle, handleDescriptor);
            }

            if (OperatingSystem.IsWindows()
                && string.Equals(handleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            {
                return Windows.Raise(handle);
            }

            if (OperatingSystem.IsLinux()
                && string.Equals(handleDescriptor, "XID", StringComparison.OrdinalIgnoreCase))
            {
                return X11.RaiseOnXfwm4WithoutActivation(handle);
            }
        }
        catch
        {
            // Raising the counterpart is best-effort and must never disrupt the
            // window the user actually activated.
            return false;
        }

        return false;
    }

    private static class MacOS
    {
        [DllImport("libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

        public static bool Raise(IntPtr handle, string handleDescriptor)
        {
            var target = string.Equals(handleDescriptor, "NSVIEW", StringComparison.OrdinalIgnoreCase)
                ? objc_msgSend(handle, sel_registerName("window"))
                : handle;
            if (target == IntPtr.Zero)
            {
                return false;
            }

            objc_msgSend_void(target, sel_registerName("orderFrontRegardless"));
            return true;
        }
    }

    private static class Windows
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        public static bool Raise(IntPtr handle) =>
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private static class X11
    {
        private const int XaWindow = 33;
        private const uint CwSibling = 1 << 5;
        private const uint CwStackMode = 1 << 6;
        private const int Above = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowChanges
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int BorderWidth;
            public IntPtr Sibling;
            public int StackMode;
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XInternAtom(
            IntPtr display,
            string atomName,
            [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

        [DllImport("libX11.so.6")]
        private static extern int XGetWindowProperty(
            IntPtr display,
            IntPtr window,
            IntPtr property,
            IntPtr longOffset,
            IntPtr longLength,
            [MarshalAs(UnmanagedType.Bool)] bool delete,
            IntPtr requestedType,
            out IntPtr actualType,
            out int actualFormat,
            out nuint itemCount,
            out nuint bytesAfter,
            out IntPtr value);

        [DllImport("libX11.so.6")]
        private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr windowName);

        [DllImport("libX11.so.6")]
        private static extern int XQueryTree(
            IntPtr display,
            IntPtr window,
            out IntPtr root,
            out IntPtr parent,
            out IntPtr children,
            out uint childCount);

        [DllImport("libX11.so.6")]
        private static extern int XConfigureWindow(
            IntPtr display,
            IntPtr window,
            uint valueMask,
            ref XWindowChanges changes);

        [DllImport("libX11.so.6")]
        private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

        [DllImport("libX11.so.6")]
        private static extern int XFree(IntPtr data);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        public static bool RaiseOnXfwm4WithoutActivation(IntPtr handle)
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (!IsXfwm4(display) || !TryGetFrameChildSibling(display, handle, out var sibling))
                {
                    return false;
                }

                // xfwm4 activates a client for a sibling-less Above request,
                // which is how XRaiseWindow is expressed. Supplying another
                // child of the target's WM frame takes xfwm4's normal restack
                // path instead, preserving focus on the activated counterpart.
                var changes = new XWindowChanges
                {
                    Sibling = sibling,
                    StackMode = Above,
                };
                if (XConfigureWindow(display, handle, CwSibling | CwStackMode, ref changes) == 0)
                {
                    return false;
                }

                _ = XSync(display, discard: false);
                return true;
            }
            finally
            {
                _ = XCloseDisplay(display);
            }
        }

        private static bool IsXfwm4(IntPtr display)
        {
            var supportingWindowProperty = XInternAtom(display, "_NET_SUPPORTING_WM_CHECK", onlyIfExists: true);
            if (supportingWindowProperty == IntPtr.Zero)
            {
                return false;
            }

            var value = IntPtr.Zero;
            try
            {
                if (XGetWindowProperty(
                        display,
                        XDefaultRootWindow(display),
                        supportingWindowProperty,
                        IntPtr.Zero,
                        new IntPtr(1),
                        delete: false,
                        new IntPtr(XaWindow),
                        out var actualType,
                        out var actualFormat,
                        out var itemCount,
                        out _,
                        out value) != 0
                    || actualType != new IntPtr(XaWindow)
                    || actualFormat != 32
                    || itemCount == 0
                    || value == IntPtr.Zero)
                {
                    return false;
                }

                var supportingWindow = Marshal.ReadIntPtr(value);
                if (supportingWindow == IntPtr.Zero
                    || XFetchName(display, supportingWindow, out var windowName) == 0
                    || windowName == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    return string.Equals(
                        Marshal.PtrToStringAnsi(windowName),
                        "xfwm4",
                        StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    _ = XFree(windowName);
                }
            }
            finally
            {
                if (value != IntPtr.Zero)
                {
                    _ = XFree(value);
                }
            }
        }

        private static bool TryGetFrameChildSibling(IntPtr display, IntPtr handle, out IntPtr sibling)
        {
            sibling = IntPtr.Zero;
            if (!TryQueryTree(display, handle, out var root, out var parent, out _)
                || parent == IntPtr.Zero
                || parent == root
                || !TryQueryTree(display, parent, out _, out _, out var frameChildren))
            {
                return false;
            }

            foreach (var child in frameChildren)
            {
                if (child != IntPtr.Zero && child != handle)
                {
                    sibling = child;
                    return true;
                }
            }

            return false;
        }

        private static bool TryQueryTree(
            IntPtr display,
            IntPtr window,
            out IntPtr root,
            out IntPtr parent,
            out IntPtr[] children)
        {
            children = [];
            var nativeChildren = IntPtr.Zero;
            try
            {
                if (XQueryTree(
                        display,
                        window,
                        out root,
                        out parent,
                        out nativeChildren,
                        out var childCount) == 0)
                {
                    return false;
                }

                children = new IntPtr[childCount];
                for (var i = 0; i < children.Length; i++)
                {
                    children[i] = Marshal.ReadIntPtr(nativeChildren, i * IntPtr.Size);
                }

                return true;
            }
            finally
            {
                if (nativeChildren != IntPtr.Zero)
                {
                    _ = XFree(nativeChildren);
                }
            }
        }
    }

}
