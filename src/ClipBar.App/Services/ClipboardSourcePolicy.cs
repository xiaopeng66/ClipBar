using System.IO;
using ClipBar.Core.Models;

namespace ClipBar.App.Services;

/// <summary>
/// Decides which clipboard sources are ignored: this app's own writes and the user's
/// password-manager exclusion list.
/// </summary>
/// <remarks>
/// Extracted from <c>ClipboardMonitor</c> so the privacy rule behind plan §5.3 T-45 is
/// verifiable without a live KeePassXC window: a secret copied from an excluded app must never
/// reach the history database, and the exclusion list is user-editable text, so it has to
/// tolerate paths, stray whitespace, casing and blank entries.
/// </remarks>
public static class ClipboardSourcePolicy
{
    /// <summary>The process name this app runs under; its own writes must not be re-captured.</summary>
    public const string SelfProcessName = "ClipBar";

    /// <summary>
    /// Chooses the source captured with a clipboard update. The clipboard owner is authoritative;
    /// foreground is only a fallback for applications that do not expose an owner window.
    /// </summary>
    public static string? ResolveSource(string? clipboardOwnerProcessName, string? foregroundProcessName)
        => string.IsNullOrWhiteSpace(clipboardOwnerProcessName)
            ? foregroundProcessName
            : clipboardOwnerProcessName;

    /// <summary>True when a clip from <paramref name="processName"/> must not be recorded.</summary>
    public static bool IsIgnored(string? processName, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        if (string.Equals(processName, SelfProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsExcluded(processName, settings);
    }

    /// <summary>True when <paramref name="processName"/> matches any user exclusion entry.</summary>
    public static bool IsExcluded(string? processName, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var excluded in settings.ExcludedApps)
        {
            // The list is free text: users type "KeePassXC.exe", "keepassxc", or paste a full
            // path. Compare on the file stem so all three forms work.
            var name = Path.GetFileNameWithoutExtension(excluded.Trim());
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
