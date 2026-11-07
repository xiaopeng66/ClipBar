using System.Runtime.InteropServices;

namespace ClipBar.App.Platform;

/// <summary>
/// Minimal Raw Input interop for the native floating clipboard bar. Raw Input is what
/// SnipDo 3.0.88.0 uses (see docs/PLAN-NATIVE-FLOATING-CLIPBOARD.md): unlike a low-level mouse
/// hook it never injects into another process's input path and cannot be slowed down by a busy
/// UI thread, so a long-press detector built on it stays out of the way.
/// </summary>
internal static class RawInputInterop
{
    public const int WmInput = 0x00FF;

    public const uint RidInput = 0x10000003;

    /// <summary>RIDEV_INPUTSINK: receive input even when this window is not in the foreground.</summary>
    public const uint RidevInputSink = 0x00000100;

    public const ushort HidUsagePageGeneric = 0x01;

    public const ushort HidUsageMouse = 0x02;

    public const uint RawInputTypeMouse = 0;

    private const uint MouseLeftButtonDown = 0x0001;
    private const uint MouseLeftButtonUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    /// <summary>
    /// RAWMOUSE. The flag/button union is a ULONG, so it starts at offset 4: the explicit
    /// padding field is what makes ButtonFlags land there (reading it at offset 2 produced
    /// garbage and the gesture never saw a button press).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort Padding;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    /// <summary>
    /// Registers the mouse for background input, targeted at <paramref name="hwnd"/>. Returns
    /// false (and leaves the last error) when registration is refused, e.g. on a stripped-down
    /// session; the caller then keeps the feature off instead of half-working.
    /// </summary>
    public static bool RegisterMouse(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = HidUsagePageGeneric,
                Usage = HidUsageMouse,
                Flags = RidevInputSink,
                Target = hwnd,
            },
        };

        return RegisterRawInputDevices(
            devices,
            1,
            (uint)Marshal.SizeOf<RawInputDevice>());
    }

    /// <summary>
    /// Decodes a WM_INPUT mouse message. Returns false for non-mouse input or a malformed packet.
    /// </summary>
    public static bool TryReadMouse(IntPtr lParam, out ushort buttonFlags)
    {
        buttonFlags = 0;

        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        var probe = GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize);
        if (probe != 0 || size < headerSize)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(lParam, RidInput, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RawInputTypeMouse)
            {
                return false;
            }

            var mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(buffer, (int)headerSize));
            buttonFlags = mouse.ButtonFlags;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsLeftDown(ushort buttonFlags) => (buttonFlags & MouseLeftButtonDown) != 0;

    public static bool IsLeftUp(ushort buttonFlags) => (buttonFlags & MouseLeftButtonUp) != 0;
}
