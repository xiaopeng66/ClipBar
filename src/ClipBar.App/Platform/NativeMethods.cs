using System.Runtime.InteropServices;

namespace ClipBar.App.Platform;

internal static class NativeMethods
{
    public const int WmClipboardUpdate = 0x031D;
    public const int WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    public const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>MDT_EFFECTIVE_DPI: the scale the monitor is actually running at.</summary>
    public const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static extern int GetDpiForMonitorNative(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Scale factor (1.0 = 96 DPI) for the monitor; falls back to the primary monitor's scale.</summary>
    public static double MonitorScale(IntPtr monitor)
    {
        // This runs on the panel-show path, where an exception means no panel at all. A missing
        // export (the historical EntryPointNotFoundException on shcore.dll) or a pre-10 1607
        // GetDpiForWindow must degrade to "assume 100%", not surface as an unhandled UI error.
        try
        {
            if (monitor != IntPtr.Zero
                && GetDpiForMonitorNative(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }

        try
        {
            var fallback = GetDpiForWindow(GetDesktopWindow());
            return fallback > 0 ? fallback / 96.0 : 1.0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return 1.0;
        }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    // ---- Low-level mouse hook (click-outside dismissal) ------------------------------

    public const int WhMouseLl = 14;
    public const int WmLButtonDown = 0x0201;
    public const int WmRButtonDown = 0x0204;
    public const int WmMButtonDown = 0x0207;
    public const int WmXButtonDown = 0x020B;

    /// <summary>Raw physical-pixel coordinates of a low-level mouse event.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MsLlHookStruct
    {
        public int X;
        public int Y;
    }

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    // ---- DWM cloaking (flash-free show) ----------------------------------------------

    /// <summary>DWMWA_CLOAK: hides the window from DWM composition without touching WPF.</summary>
    public const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>DWMWA_BORDER_COLOR (Win11): the 1px system border around a window.</summary>
    public const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_COLOR_NONE: do not draw the system border at all.</summary>
    public const uint DwmColorNone = 0xFFFFFFFE;

    /// <summary>DWMWA_NCRENDERING_POLICY: turns the DWM-drawn frame/shadow on or off.</summary>
    public const int DwmwaNcRenderingPolicy = 2;

    /// <summary>DWMNCRP_DISABLED.</summary>
    private const int DwncDisabled = 1;

    /// <summary>DWMNCRP_ENABLED.</summary>
    private const int DwncEnabled = 2;

    /// <summary>
    /// Removes the Win11 system border (a ~1px light hairline on dark windows). The panel,
    /// menus and dialogs draw their own border; leaving the system one in place is what made
    /// the panel look like it had a white edge.
    /// </summary>
    public static void RemoveSystemBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var color = unchecked((int)DwmColorNone);
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Enables/disables DWM non-client rendering, i.e. the system frame and drop shadow that
    /// DWM adds around a top-level window. Popup menus draw their own border, so the extra
    /// system shadow only made the panel underneath look like it had a thicker border.
    /// </summary>
    public static void SetNonClientRendering(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = enabled ? DwncEnabled : DwncDisabled;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Cloaks/uncloaks a window. While cloaked, DWM composites nothing for it, yet WPF keeps
    /// laying out and rendering into it — so the first visible frame is already the finished
    /// content (the technique STranslate and other popup tools use to kill the show flicker).
    /// </summary>
    public static void SetWindowCloaked(IntPtr hwnd, bool cloaked)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = cloaked ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Win8 or a stripped-down DWM: the animation simply runs without cloaking.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    public static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    public static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, index, value);
        }
        else
        {
            SetWindowLong32(hwnd, index, value.ToInt32());
        }
    }

    /// <summary>Executable name (without .exe) of the process owning the window.</summary>
    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // GetWindowThreadProcessId returns the *thread* id; the process id arrives through the
        // out parameter. Passing the return value here looked up an unrelated process.
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }
}
