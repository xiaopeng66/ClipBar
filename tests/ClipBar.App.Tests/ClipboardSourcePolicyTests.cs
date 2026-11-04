using ClipBar.App.Services;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-45 / T-46: a secret copied in an excluded app must never enter the history, and
/// the exclusion list is free text the user can type in several shapes.
/// </summary>
public class ClipboardSourcePolicyTests
{
    private static AppSettings Settings(params string[] excluded)
        => new() { ExcludedApps = excluded.ToList() };

    [Fact]
    public void TheAppsOwnWritesAreIgnored()
    {
        // Pasting from ClipBar puts content back on the clipboard; recording it again would
        // duplicate every paste and pin it to the top.
        Assert.True(ClipboardSourcePolicy.IsIgnored("ClipBar", Settings()));
        Assert.True(ClipboardSourcePolicy.IsIgnored("clipbar", Settings()));
    }

    [Fact]
    public void AnExcludedPasswordManagerIsIgnored()
    {
        var settings = Settings("KeePassXC.exe");

        Assert.True(ClipboardSourcePolicy.IsIgnored("KeePassXC", settings));
        Assert.True(ClipboardSourcePolicy.IsIgnored("KEEPassXC", settings));
    }

    [Fact]
    public void TheDefaultExclusionListCoversTheCommonManagers()
    {
        // The shipped defaults are the promise T-45 checks; keep them in sync with AppSettings.
        var defaults = new AppSettings().ExcludedApps;

        foreach (var name in new[] { "1Password", "Bitwarden", "KeePass", "KeePassXC", "Keeper" })
        {
            Assert.True(
                ClipboardSourcePolicy.IsIgnored(name, new AppSettings()),
                $"{name} 应当默认被排除");
        }

        Assert.NotEmpty(defaults);
    }

    [Fact]
    public void AnOrdinaryAppIsNotIgnored()
    {
        Assert.False(ClipboardSourcePolicy.IsIgnored("notepad", Settings("KeePassXC.exe")));
        Assert.False(ClipboardSourcePolicy.IsExcluded("notepad", Settings("KeePassXC.exe")));
    }

    [Fact]
    public void ExclusionEntriesMayBePathsOrHaveStrayWhitespace()
    {
        var settings = Settings(@"  C:\Program Files\KeePassXC\KeePassXC.exe  ", "bitwarden.exe");

        Assert.True(ClipboardSourcePolicy.IsIgnored("KeePassXC", settings));
        Assert.True(ClipboardSourcePolicy.IsIgnored("Bitwarden", settings));
    }

    [Fact]
    public void BlankAndExtensionOnlyEntriesAreSkippedInsteadOfMatchingEverything()
    {
        // A blank row would otherwise resolve to an empty stem and, if compared loosely, match
        // every source — silently disabling clipboard history.
        var settings = Settings("", "   ", ".exe");

        Assert.False(ClipboardSourcePolicy.IsIgnored("notepad", settings));
        Assert.False(ClipboardSourcePolicy.IsIgnored("explorer", settings));
    }

    [Fact]
    public void AnUnknownSourceIsNotIgnored()
    {
        // UIPI-protected or elevated windows can leave us without a process name; that must not
        // turn into "skip everything".
        Assert.False(ClipboardSourcePolicy.IsIgnored(null, Settings("KeePassXC.exe")));
        Assert.False(ClipboardSourcePolicy.IsIgnored(string.Empty, Settings("KeePassXC.exe")));
        Assert.False(ClipboardSourcePolicy.IsIgnored("   ", Settings("KeePassXC.exe")));
    }

    [Fact]
    public void ExclusionIsNotAPrefixOrSubstringMatch()
    {
        // "Keep" must not exclude "Keeper" or "KeepassXC"; only the exact stem matches.
        var settings = Settings("Keep");

        Assert.False(ClipboardSourcePolicy.IsIgnored("Keeper", settings));
        Assert.False(ClipboardSourcePolicy.IsIgnored("KeePassXC", settings));
    }
}
