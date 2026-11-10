using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipBar.App.Platform;

/// <summary>
/// Reports whether a window's process runs elevated. A non-elevated app cannot read UI
/// Automation from, or inject Ctrl+V into, an elevated one (Windows UIPI), so the native
/// floating bar must not appear over those windows — it would look broken rather than disabled.
/// </summary>
internal static class Elevation
{
    /// <summary>TOKEN_QUERY.</summary>
    private const uint TokenQuery = 0x0008;

    /// <summary>
    /// PROCESS_QUERY_LIMITED_INFORMATION. The old value was 0x0008 (PROCESS_VM_OPERATION),
    /// which OpenProcess refuses for another user's/another integrity level's process, so every
    /// target was misread as elevated and the floating bar never appeared.
    /// </summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        out int information,
        int informationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>True when the process that owns <paramref name="hwnd"/> is elevated.</summary>
    public static bool IsWindowElevated(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            // Cannot even ask: treat as elevated so the bar stays out of the way.
            return true;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return true;
            }

            try
            {
                // A failed query must not be read as "not elevated": that would send Ctrl+V at
                // an elevated target we simply could not inspect. Treat it as elevated so the
                // paste downgrades to a plain copy (fail closed for input injection).
                if (!GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _))
                {
                    return true;
                }

                return elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>True when this process itself is elevated (then elevated targets are fine).</summary>
    public static bool IsSelfElevated()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return IsElevatedProcess(self.Handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsElevatedProcess(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _)
                && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
