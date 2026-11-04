using System.Text;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

public class HtmlFormatHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain text without a CF_HTML header")]
    public void ExtractFragmentReturnsNullWithoutOffsets(string? htmlFormat)
    {
        Assert.Null(HtmlFormatHelper.ExtractFragment(htmlFormat));
    }

    [Fact]
    public void ExtractFragmentReturnsNullWhenOffsetsAreReversed()
    {
        var buffer = "Version:0.9\r\nStartHTML:0000000100\r\nEndHTML:0000000200\r\n" +
                     "StartFragment:0000000150\r\nEndFragment:0000000120\r\n";
        Assert.Null(HtmlFormatHelper.ExtractFragment(buffer));
    }

    [Fact]
    public void ExtractFragmentReturnsNullWhenOffsetsExceedTheBuffer()
    {
        var buffer = "Version:0.9\r\nStartFragment:0000000100\r\nEndFragment:0000000900\r\nbody";
        Assert.Null(HtmlFormatHelper.ExtractFragment(buffer));
    }

    [Fact]
    public void ExtractFragmentReturnsNullWhenStartIsNegativeOrMissing()
    {
        Assert.Null(HtmlFormatHelper.ExtractFragment("StartFragment:abc\r\nEndFragment:0000000010\r\n"));
        Assert.Null(HtmlFormatHelper.ExtractFragment("EndFragment:0000000010\r\n"));
    }

    [Fact]
    public void ExtractFragmentReturnsNullWhenStartEqualsEnd()
    {
        var buffer = "StartFragment:0000000010\r\nEndFragment:0000000010\r\n";
        Assert.Null(HtmlFormatHelper.ExtractFragment(buffer));
    }

    [Fact]
    public void BuildRoundTripsThroughExtractFragment()
    {
        var fragment = "<b>中文</b> &amp; <i>emoji 🙂</i>";
        var html = HtmlFormatHelper.Build(fragment, "plain");

        Assert.StartsWith("Version:0.9\r\n", html);
        Assert.Equal(fragment, HtmlFormatHelper.ExtractFragment(html));
    }

    [Fact]
    public void BuildProducesInRangeOffsets()
    {
        var html = HtmlFormatHelper.Build("<p>hello</p>", null);
        var bytes = Encoding.UTF8.GetByteCount(html);

        var start = ReadOffset(html, "StartHTML:");
        var end = ReadOffset(html, "EndHTML:");
        Assert.InRange(start, 0, bytes);
        Assert.InRange(end, start, bytes);
        Assert.True(end > start);
    }

    [Fact]
    public void BuildKeepsMultiByteFragmentsByteAccurate()
    {
        // A DIP-era bug class: offsets computed with char counts instead of UTF-8 byte counts
        // slice mid-codepoint. Non-ASCII content must still round-trip exactly.
        var fragment = new string('中', 200) + "🙂";
        var html = HtmlFormatHelper.Build(fragment, fragment);
        Assert.Equal(fragment, HtmlFormatHelper.ExtractFragment(html));
    }

    private static int ReadOffset(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.Ordinal);
        Assert.True(index >= 0, "missing " + key);
        index += key.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return int.Parse(text.AsSpan(index, end - index));
    }
}
