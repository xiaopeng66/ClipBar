using ClipBar.Core.Security;

namespace ClipBar.Core.Tests;

public class SafeUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeReturnsNullForBlank(string? url)
    {
        Assert.Null(SafeUrl.Normalize(url));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("ms-msdt:/id")]
    [InlineData("ftp://example.com")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("\\\\server\\share\\file.exe")]
    public void NormalizeRejectsNonWebSchemes(string url)
    {
        Assert.Null(SafeUrl.Normalize(url));
    }

    [Theory]
    [InlineData("https://example.com/x\r\ny")]
    [InlineData("https://example.com\u0000")]
    [InlineData("https://example.com\u001f")]
    public void NormalizeRejectsControlCharacters(string url)
    {
        Assert.Null(SafeUrl.Normalize(url));
    }

    [Fact]
    public void NormalizeKeepsHttpAndHttps()
    {
        Assert.Equal("https://example.com/a?b=1", SafeUrl.Normalize("https://example.com/a?b=1"));
        Assert.Equal("http://example.com", SafeUrl.Normalize("http://example.com"));
        Assert.Equal("HTTP://EXAMPLE.COM", SafeUrl.Normalize("HTTP://EXAMPLE.COM"));
    }

    [Fact]
    public void NormalizeKeepsMailto()
    {
        Assert.Equal("mailto:a@b.com", SafeUrl.Normalize("mailto:a@b.com"));
    }

    [Fact]
    public void NormalizeGivesAuthorityToSchemesWithoutSlashes()
    {
        Assert.Equal("http://example.com", SafeUrl.Normalize("http:example.com"));
        Assert.Equal("https://example.com", SafeUrl.Normalize("https:/example.com"));
    }

    [Fact]
    public void NormalizePrefixesHttpsWhenNoSchemeIsPresent()
    {
        Assert.Equal("https://example.com", SafeUrl.Normalize("example.com"));
        Assert.Equal("https://example.com", SafeUrl.Normalize("//example.com"));
        Assert.Equal("https://www.example.com/path", SafeUrl.Normalize("www.example.com/path"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("HTTP")]
    [InlineData("https")]
    [InlineData("mailto")]
    public void IsAllowedAcceptsWebSchemes(string scheme)
    {
        Assert.True(SafeUrl.IsAllowed(scheme));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("ftp")]
    [InlineData("javascript")]
    [InlineData("ms-settings")]
    [InlineData("")]
    public void IsAllowedRejectsOtherSchemes(string scheme)
    {
        Assert.False(SafeUrl.IsAllowed(scheme));
    }

    [Fact]
    public void ExtractSchemeReportsDriveLettersAsFile()
    {
        Assert.Equal("file", SafeUrl.ExtractScheme(@"C:\x"));
        Assert.Equal("file", SafeUrl.ExtractScheme("z:/x"));
    }

    [Fact]
    public void ExtractSchemeLowercasesAndRejectsMalformed()
    {
        Assert.Equal("mailto", SafeUrl.ExtractScheme("MAILTO:a@b"));
        Assert.Equal("ms-settings", SafeUrl.ExtractScheme("ms-settings:"));
        Assert.Null(SafeUrl.ExtractScheme("example.com"));
        Assert.Null(SafeUrl.ExtractScheme("://example.com"));
        Assert.Null(SafeUrl.ExtractScheme("1abc://x"));
        Assert.Null(SafeUrl.ExtractScheme("a b://x"));
    }
}
