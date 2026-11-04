namespace ClipBar.Core.Security;

/// <summary>
/// Decides which URL schemes a third-party SnipDo action may hand to the shell.
/// <c>Process.Start(UseShellExecute = true)</c> honours any registered protocol handler, so an
/// imported package that emits <c>file:///C:/Windows/System32/calc.exe</c>, <c>ms-settings:</c>,
/// or <c>javascript:</c> would launch arbitrary local programs. Only web/mail schemes pass.
/// </summary>
public static class SafeUrl
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>
    /// Normalises a URL template result into something safe to launch. Returns <c>null</c> when
    /// the scheme is not on the allow list; a bare host/path gets <c>https://</c> prepended.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();

        // Control characters (newlines, NULs) are a classic way to smuggle a second argument
        // past a shell; nothing legitimate needs them in a URL.
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return null;
            }
        }

        var scheme = ExtractScheme(trimmed);
        if (scheme is null)
        {
            // A UNC reference (\\server\share\x.exe) has no scheme either, but prepending
            // https:// to it would hand a shell the very network path this check exists to
            // block, and "\" is never valid in a URL host.
            if (trimmed.Contains('\\', StringComparison.Ordinal))
            {
                return null;
            }

            // No scheme at all: treat it as a web address, the way the old code did.
            return "https://" + trimmed.TrimStart('/');
        }

        if (!IsAllowed(scheme))
        {
            return null;
        }

        // "http:example.com" (no slashes) is legal but means a relative URL; give it authority.
        if (scheme is "http" or "https" && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            return scheme + "://" + trimmed[(scheme.Length + 1)..].TrimStart('/');
        }

        return trimmed;
    }

    /// <summary>True when the scheme (without the colon) is on the allow list.</summary>
    public static bool IsAllowed(string scheme)
    {
        foreach (var allowed in AllowedSchemes)
        {
            if (string.Equals(allowed, scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the scheme of a URL, or <c>null</c> when it has none. A Windows drive letter
    /// (<c>C:\x</c>) is reported as the <c>file</c> scheme so it is rejected rather than treated
    /// as a host name.
    /// </summary>
    public static string? ExtractScheme(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }

        // "C:\..." or "C:/..." is a local path, never a web address.
        if (url.Length >= 2 && url[1] == ':' && char.IsLetter(url[0]))
        {
            return "file";
        }

        var colon = url.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        // A scheme must be letters/digits/+/-/. and start with a letter.
        if (!char.IsLetter(url[0]))
        {
            return null;
        }

        for (var i = 0; i < colon; i++)
        {
            var ch = url[i];
            if (!char.IsLetterOrDigit(ch) && ch is not ('+' or '-' or '.'))
            {
                return null;
            }
        }

        return url[..colon].ToLowerInvariant();
    }
}
