using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

/// <summary>
/// The built-in 用 Bing 搜索 action replaced the imported Bing SnipDo package. Its visibility is
/// a normal setting: on by default, part of the dirty check, and preserved by Clone.
/// </summary>
public class BingSearchSettingTests
{
    [Fact]
    public void BingSearchIsOnByDefault()
    {
        Assert.True(new AppSettings().ShowBingSearch);
    }

    [Fact]
    public void TurningBingSearchOffMakesTheFormDirty()
    {
        var baseline = new AppSettings().Normalize();
        var changed = baseline.Clone();
        changed.ShowBingSearch = false;

        Assert.False(baseline.ContentEquals(changed));
    }

    [Fact]
    public void CloneKeepsTheBingSearchSetting()
    {
        var clone = new AppSettings { ShowBingSearch = false }.Clone();
        Assert.False(clone.ShowBingSearch);
    }
}
