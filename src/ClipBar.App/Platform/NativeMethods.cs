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

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardOwner();

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveType(string rootPathName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>
    /// True when the point sits on an in-place rename edit: an Edit control whose ancestor chain
    /// reaches a <c>SysListView32</c> (the desktop's icon view and Explorer's details view both
    /// host their rename box this way). Clicks there belong to the user's rename session, not to
    /// "outside".
    /// </summary>
    public static bool IsInPlaceRenameEdit(int x, int y)
    {
        var hit = WindowFromPoint(new Point(x, y));
        for (var hop = 0; hop < 6 && hit != IntPtr.Zero; hop++)
        {
            var className = ClassNameOf(hit);
            if (className is "SysListView32" or "DirectUIHWND")
            {
                return true;
            }

            // The Edit is a direct child of the list view on the desktop; Explorer's own windows
            // reach it through one or two more shells. Climb while it still looks like a shell
            // chain, and stop at anything unrelated.
            if (className is not ("Edit" or "SHELLDLL_DefView" or "DirectUIHWND" or "CtrlNotifySink"))
            {
                return false;
            }

            hit = GetAncestor(hit, GetAncestorParent);
        }

        return false;
    }

    internal static string ClassNameOf(IntPtr hwnd)
    {
        Span<char> buffer = stackalloc char[64];
        var length = GetClassName(hwnd, ref buffer[0], buffer.Length);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, ref char buffer, int maxCount);

    public const uint GetAncestorParent = 1;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public const int CwpEdit = 0x0080;

    /// <summary>LWA_ALPHA: the layered window's alpha comes from the bAlpha argument.</summary>
    public const uint LwaAlpha = 0x2;

    /// <summary>WS_EX_LAYERED: the window is composited per-pixel, enabling alpha fades.</summary>
    public const int WsExLayered = 0x00080000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint dwFlags);

    /// <summary>
    /// True when any key except the Ctrl modifiers is physically down (GetAsyncKeyState's
    /// high-order state bit). Waiting for other held keys to release prevents them from merging
    /// with the injected Ctrl+V.
    /// </summary>
    public static bool IsKeyPhysicallyDown(short state)
        => (state & unchecked((short)0x8000)) != 0;

    public static bool IsAnyOtherKeyDown()
    {
        for (var key = 1; key < 256; key++)
        {
            if (!IsKeyPhysicallyDown(GetAsyncKeyState(key)))
            {
                continue;
            }

            if (key is 0x11 or 0xA2 or 0xA3) // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
            {
                continue;
            }

            return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);


    public const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

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

    // ---- power throttling (EcoQoS) ------------------------------------------------------

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        int size);

    /// <summary>
    /// Opts this process out of Windows' background execution-speed throttling (EcoQoS).
    /// A tray app is idle most of the time; without this, the first operation after a long
    /// idle period can be silently slowed down by the scheduler. Best effort: older builds
    /// simply keep the default behaviour.
    /// </summary>
    public static void DisablePowerThrottling()
    {
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = 1,
                ControlMask = PowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            SetProcessInformation(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    // ---- Physical placement -----------------------------------------------------------

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Moves a window using physical pixels. WPF's Left/Top are device-independent and get
    /// converted with the DPI of the monitor the window is *currently* on — for a window that has
    /// never been shown that is the primary monitor's scale, so on a mixed-DPI desktop the popup
    /// landed away from the pointer. Physical placement removes the conversion entirely.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    // ---- Cursor shape (gesture gate) ---------------------------------------------------

    /// <summary>CURSORINFO: the handle of the cursor currently drawn by the foreground thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point ScreenPosition;
    }

    /// <summary>CURSOR_SHOWING: the cursor is actually on screen.</summary>
    public const int CursorShowing = 0x00000001;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref CursorInfo info);

    /// <summary>
    /// LoadCursor with a predefined ID returns the process-wide shared handle, so the value can be
    /// compared against <c>GetCursorInfo</c>'s answer. IDC_IBEAM = 32513.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    /// <summary>The DPI awareness context a window was created with, for SetThreadDpiAwarenessContext.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public const int IdiArrow = 32512;
    public const int IdiIBeam = 32513;
    public const int IdiWait = 32514;
    public const int IdiCross = 32515;
    public const int IdiUpArrow = 32516;
    public const int IdiSizeNwse = 32642;
    public const int IdiSizeNesw = 32643;
    public const int IdiSizeWe = 32644;
    public const int IdiSizeNs = 32645;
    public const int IdiSizeAll = 32646;
    public const int IdiNo = 32648;
    public const int IdiHand = 32649;
    public const int IdiAppStarting = 32650;
    public const int IdiHelp = 32651;


    // ---- DWM cloaking (flash-free show) ----------------------------------------------

    /// <summary>DWMWA_CLOAK: hides the window from DWM composition without touching WPF.</summary>
    public const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>DWMWA_BORDER_COLOR (Win11): the 1px system border around a window.</summary>
    public const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE (Win11): rounding of the window corners.</summary>
    public const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND: rounded corners matching the system style.</summary>
    private const int CornerPreferenceRound = 2;

    /// <summary>Rounds the window's corners the way the system draws menus and dialogs.</summary>
    public static void SetRoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = CornerPreferenceRound;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>DWMWA_COLOR_NONE: do not draw the system border at all.</summary>
    public const uint DwmColorNone = 0xFFFFFFFE;

    /// <summary>DWMWA_NCRENDERING_POLICY: turns the DWM-drawn frame/shadow on or off.</summary>
    public const int DwmwaNcRenderingPolicy = 2;

    /// <summary>DWMNCRP_DISABLED.</summary>
    private const int DwncDisabled = 1;

    /// <summary>DWMNCRP_ENABLED.</summary>
    private const int DwncEnabled = 2;

    /// <summary>
    /// Pins the system border to one colour. A fixed colour (instead of the active/inactive
    /// default) keeps the window's frame identical in every activation state; DWMWA_COLOR_NONE
    /// made the frame transparent, which showed the desktop through it as a light band.
    /// </summary>
    public static void SetBorderColor(IntPtr hwnd, uint colorRef)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var color = unchecked((int)colorRef);
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
