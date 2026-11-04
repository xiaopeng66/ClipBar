using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

/// <summary>
/// Locks the dirty-state contract behind the settings window's 应用 button: the button is filled
/// exactly while the form differs from the live settings, so the comparison has to ignore edits
/// that a save would canonicalise away, and has to notice every real one.
/// </summary>
public class AppSettingsComparisonTests
{
    [Fact]
    public void IdenticalSettings_AreEqual()
    {
        var left = new AppSettings();
        var right = new AppSettings();

        Assert.True(left.ContentEquals(right));
    }

    [Fact]
    public void Clone_IsEqual()
    {
        var original = new AppSettings { Hotkey = "Ctrl+Alt+V", MaxItems = 42, Theme = "Dark" };

        Assert.True(original.Clone().ContentEquals(original));
    }

    [Fact]
    public void WhitespaceAndDuplicates_AreNormalisedAway()
    {
        var live = new AppSettings
        {
            Hotkey = "Win+Alt+V",
            ExcludedApps = ["1Password.exe", "Bitwarden.exe"],
        };

        var edited = new AppSettings
        {
            Hotkey = "  Win+Alt+V  ",
            // Same apps, different casing and order of stray blanks, plus a duplicate.
            ExcludedApps = ["1password.EXE", "", "  ", "Bitwarden.exe", "1Password.exe"],
        };

        Assert.True(edited.ContentEquals(live));
    }

    [Fact]
    public void ItemCountOutsideRange_IsClampedBeforeComparing()
    {
        var live = new AppSettings { MaxItems = 10 };

        var edited = new AppSettings { MaxItems = 1 };

        Assert.True(edited.ContentEquals(live));
    }

    [Theory]
    [InlineData("Hotkey")]
    [InlineData("MaxItems")]
    [InlineData("Theme")]
    [InlineData("ImagePreview")]
    [InlineData("PasteOnClick")]
    [InlineData("StartWithWindows")]
    [InlineData("CaptureImages")]
    [InlineData("CaptureFiles")]
    [InlineData("CaptureHtml")]
    [InlineData("ExcludedApps")]
    public void EveryFieldIsPartOfTheComparison(string field)
    {
        var live = new AppSettings();
        var edited = new AppSettings();

        switch (field)
        {
            case "Hotkey": edited.Hotkey = "Ctrl+Alt+V"; break;
            case "MaxItems": edited.MaxItems = 600; break;
            case "Theme": edited.Theme = "Light"; break;
            case "ImagePreview": edited.ImagePreview = "Small"; break;
            case "PasteOnClick": edited.PasteOnClick = false; break;
            case "StartWithWindows": edited.StartWithWindows = true; break;
            case "CaptureImages": edited.CaptureImages = false; break;
            case "CaptureFiles": edited.CaptureFiles = false; break;
            case "CaptureHtml": edited.CaptureHtml = false; break;
            case "ExcludedApps": edited.ExcludedApps = ["1Password.exe"]; break;
        }

        Assert.False(edited.ContentEquals(live), $"{field} must be detected as an edit");
    }

    [Theory]
    [InlineData("Large", "Large")]
    [InlineData("Medium", "Medium")]
    [InlineData("Small", "Small")]
    [InlineData(" medium ", "Medium")]
    [InlineData("Original", "Large")]
    [InlineData("Thumbnail", "Medium")]
    [InlineData("nonsense", "Large")]
    [InlineData("", "Large")]
    public void ImagePreviewNormalisesToTheThreeSupportedSizes(string stored, string expected)
    {
        var settings = new AppSettings { ImagePreview = stored };

        Assert.Equal(expected, settings.Normalize().ImagePreview);
    }

    [Fact]
    public void NullOther_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSettings().ContentEquals(null!));
    }

    // ---- G-03: a hand-edited file can carry explicit nulls -----------------

    [Fact]
    public void NormalizeToleratesNullFieldsWithoutThrowing()
    {
        var settings = new AppSettings
        {
            Hotkey = null!,
            Theme = null!,
            ImagePreview = null!,
            ExcludedApps = null!,
        };

        var normalized = settings.Normalize();

        Assert.Equal(string.Empty, normalized.Hotkey);
        Assert.Equal(string.Empty, normalized.Theme);
        Assert.Equal("Large", normalized.ImagePreview);
        Assert.Empty(normalized.ExcludedApps);
    }

    [Fact]
    public void ContentEqualsSurvivesNullFieldsOnBothSides()
    {
        var left = new AppSettings { Hotkey = null!, Theme = null!, ExcludedApps = null! };
        var right = new AppSettings { Hotkey = null!, Theme = null!, ExcludedApps = null! };

        Assert.True(left.ContentEquals(right));
    }
}
