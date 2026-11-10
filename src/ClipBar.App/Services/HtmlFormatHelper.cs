using System.Text;

namespace ClipBar.App.Services;

public static class HtmlFormatHelper
{
    private const string HeaderTemplate =
        "Version:0.9\r\n" +
        "StartHTML:{0:D10}\r\n" +
        "EndHTML:{1:D10}\r\n" +
        "StartFragment:{2:D10}\r\n" +
        "EndFragment:{3:D10}\r\n";

    public static string? ExtractFragment(string? htmlFormat)
    {
        if (string.IsNullOrWhiteSpace(htmlFormat))
        {
            return null;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(htmlFormat);
            var start = ReadOffset(htmlFormat, "StartFragment:");
            var end = ReadOffset(htmlFormat, "EndFragment:");
            if (start >= 0 && end > start && end <= bytes.Length)
            {
                return Encoding.UTF8.GetString(bytes, start, end - start);
            }
        }
        catch (ArgumentException)
        {
        }

        // The offsets are the only thing that tells us where the fragment begins. When they are
        // missing or out of range, the raw buffer (CF_HTML header included) is not HTML we can
        // render, so drop the HTML and keep the plain-text clip instead of storing the header.
        return null;
    }

    public static string Build(string fragment, string? plainText)
    {
        var body = $"<html><body><!--StartFragment-->{fragment}<!--EndFragment--></body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(HeaderTemplate, 0, 0, 0, 0));
        var htmlStart = headerLength;
        var fragmentStart = headerLength + Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(fragment);
        var htmlEnd = htmlStart + Encoding.UTF8.GetByteCount(body);

        var header = string.Format(HeaderTemplate, htmlStart, htmlEnd, fragmentStart, fragmentEnd);
        return header + body;
    }

    private static int ReadOffset(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return -1;
        }

        index += key.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end > index && int.TryParse(text.AsSpan(index, end - index), out var value) ? value : -1;
    }
}
