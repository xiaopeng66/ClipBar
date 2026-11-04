using System.IO;
using System.Windows.Media;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.1 T-11/T-12: hostile SVG icons. Both cases must end with <c>null</c> (or a truncated
/// drawing) instead of an exception, an unbounded parse, or a stack overflow.
/// </summary>
public class SvgIconLoaderTests
{
    private static string WriteSvg(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbar-svg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "icon.svg");
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- T-11: entity expansion -----------------------------------------

    [Fact]
    public void BillionLaughsIsRejectedNotExpanded()
    {
        // Ten nested doubling levels: fully expanded this is 10^10 characters. DtdProcessing is
        // Prohibit, so the parse fails on the DOCTYPE before any entity is resolved.
        var bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [
              <!ENTITY a0 "lol">
              <!ENTITY a1 "&a0;&a0;&a0;&a0;&a0;&a0;&a0;&a0;&a0;&a0;">
              <!ENTITY a2 "&a1;&a1;&a1;&a1;&a1;&a1;&a1;&a1;&a1;&a1;">
              <!ENTITY a3 "&a2;&a2;&a2;&a2;&a2;&a2;&a2;&a2;&a2;&a2;">
              <!ENTITY a4 "&a3;&a3;&a3;&a3;&a3;&a3;&a3;&a3;&a3;&a3;">
              <!ENTITY a5 "&a4;&a4;&a4;&a4;&a4;&a4;&a4;&a4;&a4;&a4;">
              <!ENTITY a6 "&a5;&a5;&a5;&a5;&a5;&a5;&a5;&a5;&a5;&a5;">
              <!ENTITY a7 "&a6;&a6;&a6;&a6;&a6;&a6;&a6;&a6;&a6;&a6;">
              <!ENTITY a8 "&a7;&a7;&a7;&a7;&a7;&a7;&a7;&a7;&a7;&a7;">
              <!ENTITY a9 "&a8;&a8;&a8;&a8;&a8;&a8;&a8;&a8;&a8;&a8;">
              <!ENTITY a10 "&a9;&a9;&a9;&a9;&a9;&a9;&a9;&a9;&a9;&a9;">
            ]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <text>&a10;</text>
              <rect width="24" height="24" fill="black"/>
            </svg>
            """;

        var path = WriteSvg(bomb);
        try
        {
            var image = SvgIconLoader.Load(path, 24);

            // The whole document is refused, so the caller falls back to the built-in glyph.
            // The important part is that this returned at all.
            Assert.Null(image);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ExternalEntityReferenceIsNeverResolved()
    {
        // XmlResolver is null, so even with DTDs allowed there is nothing to fetch. This is the
        // shape that used to read local files into the rendered icon.
        var svg = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [ <!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini"> ]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <text>&xxe;</text>
              <rect width="24" height="24" fill="black"/>
            </svg>
            """;

        var path = WriteSvg(svg);
        try
        {
            Assert.Null(SvgIconLoader.Load(path, 24));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- T-12: deep nesting ----------------------------------------------

    [Fact]
    public void DeeplyNestedGroupsAreTruncatedWithoutOverflowing()
    {
        // 40 levels of <g> with a single rect at the bottom. Walk stops at depth 9, so the rect
        // is never reached and there is no geometry — null, not a stack overflow.
        var open = string.Concat(Enumerable.Repeat("<g>", 40));
        var close = string.Concat(Enumerable.Repeat("</g>", 40));
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              {open}<rect width="24" height="24" fill="black"/>{close}
            </svg>
            """;

        var path = WriteSvg(svg);
        try
        {
            Assert.Null(SvgIconLoader.Load(path, 24));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ShallowNestingStillRenders()
    {
        // The truncation above must not have made ordinary icons unusable: a group 3 deep with
        // a shape inside has to produce a drawing.
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <g><g><g><rect width="24" height="24" fill="#000"/></g></g></g>
            </svg>
            """;

        var path = WriteSvg(svg);
        try
        {
            var image = SvgIconLoader.Load(path, 24);
            Assert.NotNull(image);
            Assert.IsType<DrawingImage>(image);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- malformed / hostile input --------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<svg")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M 0 0 L\"/></svg>")]
    [InlineData("<div><rect width=\"4\" height=\"4\" fill=\"#000\"/></div>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    public void MalformedOrEmptySvgYieldsNull(string content)
    {
        var path = WriteSvg(content);
        try
        {
            Assert.Null(SvgIconLoader.Load(path, 24));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MissingFileYieldsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "clipbar-nope-" + Guid.NewGuid().ToString("N")[..8] + ".svg");
        Assert.Null(SvgIconLoader.Load(path, 24));
    }

    [Fact]
    public void AbsurdPathDataIsDroppedInsteadOfThrowing()
    {
        // A huge path string is a cheap way to blow up Geometry.Parse; it must fail per-shape,
        // not abort the whole icon.
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <path d="{new string('9', 20000)}" fill="#000"/>
              <rect width="24" height="24" fill="#000"/>
            </svg>
            """;

        var path = WriteSvg(svg);
        try
        {
            var image = SvgIconLoader.Load(path, 24);
            Assert.NotNull(image);  // the rect still renders
        }
        finally
        {
            Cleanup(path);
        }
    }
}
