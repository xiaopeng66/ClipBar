using System.IO;

namespace ClipBar.Core.Security;

/// <summary>
/// Path helpers for everything that reads a third-party extension package. Extension manifests
/// come from the internet, so every identifier and every relative file reference is treated as
/// hostile input: it may contain <c>.</c>, <c>..</c>, absolute paths, or reserved device names.
/// </summary>
public static class SafePath
{
    /// <summary>Longest folder/file name segment we will accept for an imported extension.</summary>
    public const int MaxSegmentLength = 64;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Turns an arbitrary manifest identifier into a single, safe folder name.
    /// Returns <c>null</c> when the identifier cannot be salvaged into anything usable, so the
    /// caller can fall back to a generated name instead of guessing.
    /// </summary>
    public static string? Slug(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(identifier.Length);
        foreach (var ch in identifier.Trim())
        {
            if (char.IsControl(ch) || invalid.Contains(ch))
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString().Trim(' ', '.', '-', '_');

        // "." and ".." are not rejected by GetInvalidFileNameChars, yet they are the two names
        // that turn Directory.Delete(recursive) loose on the store's parent directories.
        if (cleaned.Length == 0 || IsDotsOnly(cleaned))
        {
            return null;
        }

        if (ReservedNames.Contains(cleaned))
        {
            return null;
        }

        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength].TrimEnd(' ', '.', '-', '_');
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Returns a folder name for an identifier, generating one when the identifier is unusable.
    /// </summary>
    public static string SlugOrFallback(string? identifier, string fallbackPrefix = "extension")
    {
        var slug = Slug(identifier);
        return slug ?? fallbackPrefix + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Resolves <paramref name="candidate"/> against <paramref name="root"/> and asserts the
    /// result is strictly inside the root. Throws <see cref="UnauthorizedAccessException"/>
    /// when the reference escapes (absolute path, drive change, or <c>..</c> segment).
    /// </summary>
    public static string EnsureInside(string root, string candidate, string what = "路径")
    {
        var rootFull = NormalizeRoot(root);
        var trimmed = candidate.Trim();
        if (trimmed.Length == 0)
        {
            throw new UnauthorizedAccessException($"{what}为空。");
        }

        // A rooted reference is never legitimate for a package-relative field: it lets a
        // manifest point at C:\Windows\System32 or at a UNC share.
        if (Path.IsPathRooted(trimmed))
        {
            throw new UnauthorizedAccessException($"{what}不允许使用绝对路径：{candidate}");
        }

        var combined = Path.GetFullPath(Path.Combine(rootFull, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(rootFull, combined))
        {
            throw new UnauthorizedAccessException($"{what}越出扩展目录：{candidate}");
        }

        return combined;
    }

    /// <summary>Like <see cref="EnsureInside"/>, but returns <c>null</c> instead of throwing.</summary>
    public static string? TryResolveInside(string root, string? candidate, string what = "路径")
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return EnsureInside(root, candidate, what);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="root"/>.</summary>
    public static bool IsInside(string root, string path)
    {
        var rootFull = NormalizeRoot(root);
        var pathFull = Path.GetFullPath(path);

        // GetFullPath preserves a trailing separator ("C:\store\" stays as typed). Left alone,
        // the prefix test below reports the root itself as its own child, and DeleteInside
        // would then recursively delete the extension store.
        if (pathFull.Length > rootFull.Length)
        {
            pathFull = pathFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (string.Equals(rootFull, pathFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        return pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes a directory tree, but only when it is a real child of <paramref name="root"/>.
    /// Guards every recursive delete so a bad identifier can never wipe the store itself.
    /// </summary>
    public static bool DeleteInside(string root, string target)
    {
        var full = Path.GetFullPath(target);
        if (!IsInside(root, full) || !Directory.Exists(full))
        {
            return false;
        }

        Directory.Delete(full, recursive: true);
        return true;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // "C:\" has to keep its separator: trimming it yields "C:", a *relative* drive reference
        // that makes every prefix comparison below wrong.
        return trimmed.Length == 2 && trimmed[1] == ':' ? full : trimmed;
    }

    private static bool IsDotsOnly(string value)
    {
        foreach (var ch in value)
        {
            if (ch != '.')
            {
                return false;
            }
        }

        return true;
    }
}
