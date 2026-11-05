using ClipBar.App.Services;
using ClipBar.App.ViewModels;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>A7/A8 pure helpers: search-term splitting and the time/source filters.</summary>
public class SearchAndFilterTests
{
    [Fact]
    public void SplittingWithoutATermKeepsTheWholeTitle()
    {
        var segments = TextHighlighter.Split("hello world", null);

        Assert.Single(segments);
        Assert.Equal(("hello world", false), segments[0]);
    }

    [Fact]
    public void SplittingMarksEveryCaseInsensitiveHit()
    {
        var segments = TextHighlighter.Split("Clip and clip", "clip");

        Assert.Equal(3, segments.Count);
        Assert.Equal(("Clip", true), segments[0]);
        Assert.Equal((" and ", false), segments[1]);
        Assert.Equal(("clip", true), segments[2]);
    }

    [Fact]
    public void SplittingHandlesANonMatchingTerm()
    {
        var segments = TextHighlighter.Split("hello", "zzz");

        Assert.Single(segments);
        Assert.Equal(("hello", false), segments[0]);
    }

    [Fact]
    public void SplittingHandlesChineseTerms()
    {
        var segments = TextHighlighter.Split("图片 1847×1217", "图片");

        Assert.Equal(("图片", true), segments[0]);
    }

    [Fact]
    public void TimeFilterAllKeepsEveryRow()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var old = DateTimeOffset.FromUnixTimeMilliseconds(0).ToUnixTimeMilliseconds();

        Assert.True(MainViewModel.MatchesTimeFilter(old, 0, now));
    }

    [Fact]
    public void TimeFilterTodayKeepsOnlyTodaysRows()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var today = new DateTime(2026, 11, 5, 8, 0, 0);
        var yesterday = new DateTime(2026, 11, 4, 8, 0, 0);

        Assert.True(MainViewModel.MatchesTimeFilter(
            new DateTimeOffset(today).ToUnixTimeMilliseconds(), 1, now));
        Assert.False(MainViewModel.MatchesTimeFilter(
            new DateTimeOffset(yesterday).ToUnixTimeMilliseconds(), 1, now));
    }

    [Fact]
    public void TimeFilterWeekKeepsRowsInsideTheWindow()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var threeDaysAgo = new DateTime(2026, 11, 2, 12, 0, 0);
        var tenDaysAgo = new DateTime(2026, 10, 26, 12, 0, 0);

        Assert.True(MainViewModel.MatchesTimeFilter(
            new DateTimeOffset(threeDaysAgo).ToUnixTimeMilliseconds(), 7, now));
        Assert.False(MainViewModel.MatchesTimeFilter(
            new DateTimeOffset(tenDaysAgo).ToUnixTimeMilliseconds(), 7, now));
    }

    [Theory]
    [InlineData("msedge.exe", "Msedge")]
    [InlineData(null, "未知来源")]
    public void SourceDisplayNameMatchesTheSubtitleStyle(string? app, string expected)
    {
        var item = new ClipItem { Kind = ClipKind.Text, Text = "x", SourceApp = app };

        Assert.Equal(expected, MainViewModel.SourceDisplayName(item));
    }

    [Fact]
    public void SourceFilterMatchesTheSelectedAppOnly()
    {
        var edge = new ClipItem { Kind = ClipKind.Text, Text = "x", SourceApp = "msedge.exe" };
        var word = new ClipItem { Kind = ClipKind.Text, Text = "y", SourceApp = "winword.exe" };

        Assert.True(MainViewModel.MatchesSourceFilter(edge, "全部来源"));
        Assert.True(MainViewModel.MatchesSourceFilter(edge, "Msedge"));
        Assert.False(MainViewModel.MatchesSourceFilter(word, "Msedge"));
    }
}
