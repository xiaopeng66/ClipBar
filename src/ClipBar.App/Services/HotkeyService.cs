using System.Windows.Input;
using System.Windows.Interop;
using ClipBar.App.Platform;

namespace ClipBar.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4C11;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;
    private string? _current;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("ClipBar.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    public bool Register(string hotkey, out string error)
    {
        error = string.Empty;

        // Validate first: the old code called Unregister() before TryParse, so a typo or an
        // already-occupied combo left the app with no hotkey at all and no way back except a
        // restart. Parse before touching the live registration.
        if (!TryParse(hotkey, out var modifiers, out var virtualKey, out error))
        {
            return false;
        }

        if (_registered && string.Equals(_current, hotkey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Release the old combo and bind the new one; if the new one is refused, re-bind the
        // previous combo so a rejected change never leaves the app without a working hotkey.
        var previous = _registered ? _current : null;
        Unregister();

        if (!NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey))
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = code == 1409
                ? "这个快捷键已经被其他程序占用。"
                : $"注册快捷键失败（错误码 {code}）。";

            // Put the previous combo back so the app stays usable.
            if (previous is not null
                && TryParse(previous, out var oldModifiers, out var oldVirtualKey, out _)
                && NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, oldModifiers, oldVirtualKey))
            {
                _registered = true;
                _current = previous;
            }

            return false;
        }

        _registered = true;
        _current = hotkey;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public static bool TryParse(string hotkey, out uint modifiers, out uint virtualKey, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "快捷键不能为空。";
            return false;
        }

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "快捷键至少需要一个修饰键和一个按键，例如 Win+Shift+V。";
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "win":
                case "windows":
                case "super":
                    modifiers |= NativeMethods.ModWin;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    break;
                default:
                    error = $"不认识的修饰键：{parts[i]}";
                    return false;
            }
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
        {
            error = $"不认识的按键：{keyName}";
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = $"不支持的按键：{keyName}";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.Dispose();
    }
}
