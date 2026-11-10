using System.IO;
using ClipBar.App.Platform;

namespace ClipBar.App.Services;

/// <summary>Rejects network-backed file paths before clipboard capture probes the filesystem.</summary>
internal static class ClipboardPathPolicy
{
    private const uint DriveRemote = 4;

    public static bool IsNetworkPath(string? path, Func<string, uint> getDriveType)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Windows accepts either slash as a path separator. Reject every pair of leading
        // separators (including mixed forms like \\/ and /\\) before asking the OS about a
        // root, so a crafted file-drop cannot trigger an SMB probe or credential negotiation.
        if (path.Length >= 2
            && IsSeparator(path[0])
            && IsSeparator(path[1]))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (root.Length == 2 && root[1] == Path.VolumeSeparatorChar)
        {
            root += Path.DirectorySeparatorChar;
        }

        return getDriveType(root) == DriveRemote;
    }

    private static bool IsSeparator(char value)
        => value is '\\' or '/';

    public static bool IsNetworkPath(string? path)
        => IsNetworkPath(path, NativeMethods.GetDriveType);
}
