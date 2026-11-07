using System.Windows.Interop;
using ClipBar.App.Platform;

namespace ClipBar.App.Services;

/// <summary>
/// Feeds the floating-bar gesture recogniser from Windows Raw Input.
/// <para>
/// The service attaches to an existing window's HWND (the hidden panel handle) and raises
/// down/up/move events with screen coordinates. Raw Input is delivered outside the normal mouse
/// message path, so the events do not depend on which window the user is clicking and no
/// process-wide hook is installed.
/// </para>
/// <para>
/// The WndProc hook runs on the UI thread and must stay cheap: it decodes the packet, samples
/// the cursor position and raises the event. Anything expensive belongs to the consumer.
/// </para>
/// </summary>
public sealed class RawMouseInputService : IDisposable
{
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private bool _disposed;
    private bool _held;
    private bool _loggedFirstPacket;

    private RawMouseInputService(HwndSource source)
    {
        _source = source;
        _hook = WndProc;
        _source.AddHook(_hook);
    }

    /// <summary>Left button went down at a screen point (physical pixels).</summary>
    public event Action<ScreenPoint>? LeftButtonDown;

    /// <summary>Left button came up at a screen point.</summary>
    public event Action<ScreenPoint>? LeftButtonUp;

    /// <summary>Pointer moved while the left button was down; carries the current screen point.</summary>
    public event Action<ScreenPoint>? DragMoved;

    /// <summary>
    /// Attaches to <paramref name="hwnd"/>. Returns null when Raw Input registration fails; the
    /// feature then stays off and the rest of ClipBar is unaffected.
    /// </summary>
    public static RawMouseInputService? TryAttach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return null;
        }

        if (!RawInputInterop.RegisterMouse(hwnd))
        {
            AppLog.Write("Raw Input registration failed; native floating bar stays off");
            return null;
        }

        return new RawMouseInputService(source);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != RawInputInterop.WmInput || _disposed)
        {
            return IntPtr.Zero;
        }

        if (!RawInputInterop.TryReadMouse(lParam, out var buttons))
        {
            return IntPtr.Zero;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return IntPtr.Zero;
        }

        if (!_loggedFirstPacket)
        {
            _loggedFirstPacket = true;
            AppLog.Write("Raw Input: first mouse packet received");
        }

        if (RawInputInterop.IsLeftDown(buttons))
        {
            _held = true;
            LeftButtonDown?.Invoke(new ScreenPoint(point.X, point.Y));
        }
        else if (RawInputInterop.IsLeftUp(buttons))
        {
            var wasHeld = _held;
            _held = false;
            if (wasHeld)
            {
                LeftButtonUp?.Invoke(new ScreenPoint(point.X, point.Y));
            }
        }
        else if (_held)
        {
            DragMoved?.Invoke(new ScreenPoint(point.X, point.Y));
        }

        // Never mark the message handled: input must keep flowing to the real target.
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(_hook);
    }
}
