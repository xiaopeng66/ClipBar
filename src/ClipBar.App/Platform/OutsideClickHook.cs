using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClipBar.App.Platform;

/// <summary>
/// Global low-level mouse hook that reports clicks on windows that do not belong to this
/// process. This is how popup-style tools (Ditto, PowerToys Run, Listary) implement "click
/// outside closes": the hook bypasses activation semantics entirely, so clicks on surfaces that
/// never take the foreground — parts of the desktop, the taskbar clock — count as click-outside
/// too, and dismissal is instant (the hook fires on mouse-down, before any activation).
///
/// The callback runs on the installing (UI) thread while it pumps messages and MUST stay tiny:
/// Windows silently removes low-level hooks whose callback stalls. It samples two Win32 values
/// and posts the decision to the dispatcher; all WPF state stays on the UI thread.
/// </summary>
public sealed class OutsideClickHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc; // rooted: a collected delegate unhooks silently
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isActive;
    private readonly Action _dismiss;

    /// <summary>When set, a click inside this predicate is kept (popup variant).</summary>
    private readonly Func<int, int, bool>? _isInside;
    private readonly uint _processId;
    private readonly IntPtr _hook;
    private bool _disposed;

    private OutsideClickHook(Func<bool> isActive, Action dismiss, Func<int, int, bool>? isInside = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _isActive = isActive;
        _dismiss = dismiss;
        _isInside = isInside;
        _processId = (uint)Environment.ProcessId;
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == IntPtr.Zero)
        {
            // A failed install is a degraded mode, not a crash: Deactivated and the focus
            // watchdog still cover the normal cases. Surface it so a systematically failing
            // install is visible in the log.
            AppLog.Error(
                "Install outside-click hook failed",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>
    /// Starts reporting outside clicks. <paramref name="isActive"/> is sampled in the hook
    /// (cheap, on the UI thread): while it returns false — a menu, dialog or keep-open paste
    /// holds the panel — clicks pass through untouched and that scope's own lifecycle decides.
    /// <paramref name="dismiss"/> runs on the dispatcher for every click on a foreign window.
    /// </summary>
    public static OutsideClickHook Start(Func<bool> isActive, Action dismiss)
        => new(isActive, dismiss);

    /// <summary>
    /// Popup variant: reports every click whose point is outside the popup's own window rect,
    /// including clicks on other windows of this process and on non-activating shell surfaces
    /// (taskbar, desktop). This is what a <c>WS_EX_NOACTIVATE</c> menu needs: it never becomes
    /// active, so <c>Deactivated</c> never fires and the menu used to stay on screen until an
    /// item was chosen.
    /// </summary>
    public static OutsideClickHook StartForPopup(Func<bool> isActive, Func<int, int, bool> isInside, Action dismiss)
        => new(isActive, dismiss, isInside);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0
            && (wParam.ToInt64() is NativeMethods.WmLButtonDown
                or NativeMethods.WmRButtonDown
                or NativeMethods.WmMButtonDown
                or NativeMethods.WmXButtonDown)
            && _isActive())
        {
            var info = Marshal.PtrToStructure<NativeMethods.MsLlHookStruct>(lParam);
            if (_isInside is { } inside)
            {
                // Popup variant: keep the click only when it lands on the popup itself — unless it
                // lands on an in-place rename edit. Pressing that edit is how the user *keeps*
                // renaming (it belongs to the target app, so it always counts as "outside"), and
                // dismissing there reads as the tool breaking; it is also where a pasted row must
                // land.
                if (!inside(info.X, info.Y) && !NativeMethods.IsInPlaceRenameEdit(info.X, info.Y))
                {
                    _dispatcher.BeginInvoke(_dismiss, DispatcherPriority.Send);
                }
            }
            else
            {
                var hit = NativeMethods.WindowFromPoint(new NativeMethods.Point(info.X, info.Y));
                NativeMethods.GetWindowThreadProcessId(hit, out var hitPid);
                if (hitPid != _processId)
                {
                    _dispatcher.BeginInvoke(_dismiss, DispatcherPriority.Send);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
