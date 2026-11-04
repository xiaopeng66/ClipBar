using System.Text;
using System.Text.RegularExpressions;

namespace ClipBar.Core.Extensions;

/// <summary>Text transformations SnipDo exposes to extension actions.</summary>
public static class SnipDoText
{
    public const string PlainText = "{PANTHERBAR_PLAIN_TEXT}";
    public const string UrlEncodedText = "{PANTHERBAR_URLENCODED_TEXT}";
    public const string UrlEncodedAltText = "{PANTHERBAR_URLENCODED_ALT_TEXT}";
    public const string HtmlEncodedText = "{PANTHERBAR_HTMLENCODED_TEXT}";
    public const string AppOptionPrefix = "{APPOPTION ";

    /// <summary>
    /// Older bundles (including SnipDo's own built-in actions) use the short
    /// spelling without the PANTHERBAR_ prefix; both are accepted.
    /// </summary>
    private static readonly (string Short, string Long)[] PlaceholderAliases =
    [
        ("{PLAIN_TEXT}", PlainText),
        ("{URLENCODED_TEXT}", UrlEncodedText),
        ("{URLENCODED_ALT_TEXT}", UrlEncodedAltText),
        ("{HTMLENCODED_TEXT}", HtmlEncodedText),
    ];

    private static readonly Regex UrlPattern = new(
        @"(?i)\b(?:https?|ftp)://[^\s<>""']+|\bwww\.[^\s<>""']+",
        RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$",
        RegexOptions.Compiled);

    /// <summary>Percent-encoding where spaces become '+', as SnipDo does.</summary>
    public static string EncodeUrl(string text) =>
        Uri.EscapeDataString(text).Replace("%20", "+");

    /// <summary>Percent-encoding where spaces stay %20.</summary>
    public static string EncodeUrlAlt(string text) => Uri.EscapeDataString(text);

    public static string EncodeHtml(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");

    /// <summary>
    /// Replaces every SnipDo placeholder. Unknown placeholders are left untouched so
    /// a broken option surfaces instead of silently disappearing.
    /// </summary>
    public static string Resolve(
        string template,
        string text,
        IReadOnlyDictionary<string, string>? appOptions = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        // One pass, not a chain of Replace() calls. Sequential replaces let the *inserted*
        // clip text be expanded again: copying the literal "{PLAIN_TEXT}" used to paste the
        // clipboard twice, and any text containing a placeholder got rewritten. Scanning the
        // template left-to-right means each byte of the template is consumed exactly once and
        // replacement values are never rescanned.
        var builder = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var next = template.IndexOf('{', index);
            if (next < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, next - index);

            var replacement = MatchPlaceholder(template, next, text, appOptions, out var consumed);
            if (replacement is null)
            {
                // Not a placeholder we know: emit the brace and keep scanning from the next char.
                builder.Append('{');
                index = next + 1;
            }
            else
            {
                builder.Append(replacement);
                index = next + consumed;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// If <paramref name="start"/> begins a known placeholder, returns its replacement and the
    /// number of characters it occupies; otherwise null with <paramref name="consumed"/> 0.
    /// </summary>
    private static string? MatchPlaceholder(
        string template,
        int start,
        string text,
        IReadOnlyDictionary<string, string>? appOptions,
        out int consumed)
    {
        consumed = 0;

        foreach (var (token, value) in FixedPlaceholders(text))
        {
            if (start + token.Length <= template.Length
                && template.AsSpan(start, token.Length).SequenceEqual(token))
            {
                consumed = token.Length;
                return value;
            }
        }

        if (appOptions is { Count: > 0 }
            && template.AsSpan(start).StartsWith(AppOptionPrefix, StringComparison.Ordinal))
        {
            var close = template.IndexOf('}', start + AppOptionPrefix.Length);
            if (close > start)
            {
                var key = template[(start + AppOptionPrefix.Length)..close];
                if (appOptions.TryGetValue(key, out var optionValue))
                {
                    consumed = close + 1 - start;
                    return optionValue;
                }
            }
        }

        return null;
    }

    /// <summary>Long and short spellings, longest first so no token shadows a longer one.</summary>
    private static IEnumerable<(string Token, string Value)> FixedPlaceholders(string text)
    {
        yield return (PlainText, text);
        yield return (UrlEncodedText, EncodeUrl(text));
        yield return (UrlEncodedAltText, EncodeUrlAlt(text));
        yield return (HtmlEncodedText, EncodeHtml(text));

        foreach (var (shortForm, longForm) in PlaceholderAliases)
        {
            yield return (shortForm, longForm switch
            {
                PlainText => text,
                UrlEncodedText => EncodeUrl(text),
                UrlEncodedAltText => EncodeUrlAlt(text),
                _ => EncodeHtml(text),
            });
        }
    }

    /// <summary>First URL found in the text, if any.</summary>
    public static string? FindUrl(string text)
    {
        var match = UrlPattern.Match(text ?? string.Empty);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']', ';', ':') : null;
    }

    public static bool IsUrl(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var match = UrlPattern.Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length;
    }

    public static bool IsEmail(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length > 0 && EmailPattern.IsMatch(trimmed);
    }

    public static bool IsFilePath(string text, Func<string, bool> exists)
    {
        var trimmed = (text ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.Contains('\n'))
        {
            return false;
        }

        return exists(trimmed);
    }
}
