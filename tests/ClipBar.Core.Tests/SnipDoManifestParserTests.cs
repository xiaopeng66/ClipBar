using ClipBar.Core.Extensions;
using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

public class SnipDoManifestParserTests
{
    private const string BasicManifest = """
        {
          "name": "Google Search",
          "identifier": "community.google.search",
          "icon": "icon.png",
          "extensionTranslations": { "zh-Hans": "谷歌搜索", "en": "Google Search" },
          "actions": [
            {
              "title": "Search Google",
              "actionTranslations": { "zh-Hans": "搜索谷歌" },
              "url": "https://www.google.com/search?q={PANTHERBAR_URLENCODED_TEXT}",
              "regex": "^[^\\n]{2,}"
            },
            {
              "title": "Open link",
              "url": "{PANTHERBAR_PLAIN_TEXT}",
              "requirements": ["IS_URL"]
            }
          ]
        }
        """;

    [Fact]
    public void ParsesNameIdentifierAndActions()
    {
        var extension = SnipDoManifestParser.Parse(BasicManifest, "C:\\ext");

        Assert.Equal("Google Search", extension.Name);
        Assert.Equal("community.google.search", extension.Identifier);
        Assert.Equal(2, extension.Actions.Count);
        Assert.Equal(SnipDoActionKind.Url, extension.Actions[0].Kind);
        Assert.Equal("https://www.google.com/search?q={PANTHERBAR_URLENCODED_TEXT}", extension.Actions[0].Url);
        Assert.Equal(["IS_URL"], extension.Actions[1].Requirements);
    }

    [Fact]
    public void PrefersSimplifiedChineseTranslation()
    {
        var extension = SnipDoManifestParser.Parse(BasicManifest, "C:\\ext");

        Assert.Equal("谷歌搜索", extension.DisplayName);
        Assert.Equal("搜索谷歌", extension.Actions[0].DisplayTitle);
    }

    [Fact]
    public void FallsBackToAuthoredTitlesWhenNoTranslationMatches()
    {
        var extension = SnipDoManifestParser.Parse(
            """{ "name": "N", "identifier": "i", "actions": [ { "title": "T", "url": "u" } ] }""",
            "C:\\ext");

        Assert.Equal("N", extension.DisplayName);
        Assert.Equal("T", extension.Actions[0].DisplayTitle);
    }

    [Fact]
    public void DerivesIdentifierWhenMissing()
    {
        var extension = SnipDoManifestParser.Parse(
            """{ "name": "My Tool", "actions": [ { "title": "Go", "url": "u" } ] }""",
            "C:\\ext");

        Assert.Equal("imported.my-tool", extension.Identifier);
    }

    [Fact]
    public void RejectsManifestWithoutName()
    {
        var error = Assert.Throws<SnipDoManifestException>(
            () => SnipDoManifestParser.Parse("""{ "identifier": "x", "actions": [ { "title": "T", "url": "u" } ] }""", "C:\\ext"));

        Assert.Contains("name", error.Message);
    }

    [Fact]
    public void RejectsManifestWithoutUsableActions()
    {
        var error = Assert.Throws<SnipDoManifestException>(
            () => SnipDoManifestParser.Parse("""{ "name": "X", "identifier": "x", "actions": [] }""", "C:\\ext"));

        Assert.Contains("actions", error.Message);
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        Assert.Throws<SnipDoManifestException>(() => SnipDoManifestParser.Parse("{ not json", "C:\\ext"));
    }

    [Fact]
    public void DetectsActionKind()
    {
        var extension = SnipDoManifestParser.Parse(
            """
            { "name": "K", "identifier": "k", "actions": [
              { "title": "A", "powershellFile": "a.ps1" },
              { "title": "B", "keyCombo": "^v" },
              { "title": "C", "url": "https://x" }
            ] }
            """,
            "C:\\ext");

        Assert.Equal(SnipDoActionKind.PowerShell, extension.Actions[0].Kind);
        Assert.Equal(SnipDoActionKind.KeyCombo, extension.Actions[1].Kind);
        Assert.Equal(SnipDoActionKind.Url, extension.Actions[2].Kind);
    }

    [Fact]
    public void ParsesAppOptions()
    {
        var extension = SnipDoManifestParser.Parse(
            """
            { "name": "O", "identifier": "o", "actions": [
              { "title": "T", "url": "https://{APPOPTION site}/s?q={PANTHERBAR_URLENCODED_TEXT}",
                "options": [ { "identifier": "site", "values": { "Google": "google.com", "Bing": "bing.com" } } ] }
            ] }
            """,
            "C:\\ext");

        var option = Assert.Single(extension.Actions[0].Options);
        Assert.Equal("site", option.Identifier);
        Assert.Equal("google.com", option.Values["Google"]);
    }

    [Fact]
    public void ParsesColoredIconAndMonochromeFlag()
    {
        // Shape of a real installed SnipDo bundle (data.json in the LocalCache store).
        var extension = SnipDoManifestParser.Parse(
            """
            {
              "name": "Bing",
              "identifier": "f64864d55bac41e5868120befb5eff91",
              "icon": "2abf17c3f4.svg",
              "icon_colored": "77e0740351.svg",
              "monochrome": false,
              "actions": [ {
                "title": "Bing",
                "icon": "2abf17c3f4.svg",
                "icon_colored": "77e0740351.svg",
                "url": "http://www.bing.com/search?q={URLENCODED_TEXT}"
              } ]
            }
            """,
            "C:\\ext");

        Assert.Equal("77e0740351.svg", extension.IconColored);
        Assert.Equal("77e0740351.svg", extension.Actions[0].IconColored);
        Assert.False(extension.Monochrome);
        Assert.Equal(SnipDoActionKind.Url, extension.Actions[0].Kind);
        Assert.Equal("http://www.bing.com/search?q={URLENCODED_TEXT}", extension.Actions[0].Url);
    }

    [Fact]
    public void MonochromeFlagIsParsed()
    {
        var extension = SnipDoManifestParser.Parse(
            """
            { "name": "M", "identifier": "m", "monochrome": true,
              "actions": [ { "title": "T", "url": "https://x" } ] }
            """,
            "C:\\ext");

        Assert.True(extension.Monochrome);
    }
}
