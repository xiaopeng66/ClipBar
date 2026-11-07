using ClipBar.App.Services;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// The clip menu is first-party now: plain text, save image, and the optional Bing search.
/// SnipDo packages contribute nothing, and the Bing row disappears with its setting.
/// </summary>
public class ClipMenuBuilderTests
{
    private static ClipItem Text(string? text = "hello") => new() { Kind = ClipKind.Text, Text = text };

    [Fact]
    public void TextClipOffersPlainTextAndBingByDefault()
    {
        var entries = ClipMenuBuilder.Build(Text(), new AppSettings());

        Assert.Equal(["粘贴为纯文本", "用 Bing 搜索"], entries.Select(e => e.Title));
        Assert.Equal(
            [ClipMenuActionKind.PasteAsPlainText, ClipMenuActionKind.SearchWithBing],
            entries.Select(e => e.Kind));
    }

    [Fact]
    public void TurningTheSettingOffRemovesOnlyTheBingRow()
    {
        var entries = ClipMenuBuilder.Build(Text(), new AppSettings { ShowBingSearch = false });

        var single = Assert.Single(entries);
        Assert.Equal(ClipMenuActionKind.PasteAsPlainText, single.Kind);
    }

    [Fact]
    public void AnEmptyTextClipHasNoBingRow()
    {
        var entries = ClipMenuBuilder.Build(Text("   "), new AppSettings());
        Assert.DoesNotContain(entries, e => e.Kind == ClipMenuActionKind.SearchWithBing);
    }

    [Fact]
    public void ImageClipOffersOnlySaveAs()
    {
        var entries = ClipMenuBuilder.Build(new ClipItem { Kind = ClipKind.Image }, new AppSettings());

        var single = Assert.Single(entries);
        Assert.Equal(ClipMenuActionKind.SaveImageAs, single.Kind);
        Assert.Equal("图片另存为…", single.Title);
    }

    [Fact]
    public void FileClipsHaveNoBuiltInRows()
    {
        var entries = ClipMenuBuilder.Build(new ClipItem { Kind = ClipKind.Files }, new AppSettings());
        Assert.Empty(entries);
    }
}
