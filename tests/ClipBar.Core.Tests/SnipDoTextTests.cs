using ClipBar.Core.Extensions;
using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

public class SnipDoTextTests
{
    [Fact]
    public void ResolvesPlainText()
    {
        Assert.Equal("xa b", SnipDoText.Resolve("x{PANTHERBAR_PLAIN_TEXT}", "a b"));
    }

    [Fact]
    public void EncodesUrlWithPlusForSpaces()
    {
        Assert.Equal("hello+world%26x", SnipDoText.EncodeUrl("hello world&x"));
    }

    [Fact]
    public void EncodesUrlAltWithPercent20()
    {
        Assert.Equal("hello%20world", SnipDoText.EncodeUrlAlt("hello world"));
    }

    [Fact]
    public void EncodesHtml()
    {
        Assert.Equal("a &lt;b&gt; &quot;c&quot;", SnipDoText.EncodeHtml("a <b> \"c\""));
    }

    [Fact]
    public void ResolvesAppOptionPlaceholder()
    {
        var resolved = SnipDoText.Resolve(
            "https://{APPOPTION site}/q?={PANTHERBAR_URLENCODED_TEXT}",
            "a b",
            new Dictionary<string, string> { ["site"] = "google.com" });

        Assert.Equal("https://google.com/q?=a+b", resolved);
    }

    [Fact]
    public void ResolvesShortPlaceholdersUsedByRealBundles()
    {
        Assert.Equal(
            "http://www.bing.com/search?q=hello+world",
            SnipDoText.Resolve("http://www.bing.com/search?q={URLENCODED_TEXT}", "hello world"));
        Assert.Equal("a%20b", SnipDoText.Resolve("{URLENCODED_ALT_TEXT}", "a b"));
        Assert.Equal("a+b", SnipDoText.Resolve("{URLENCODED_TEXT}", "a b"));
        Assert.Equal("raw", SnipDoText.Resolve("{PLAIN_TEXT}", "raw"));
        Assert.Equal("&lt;x&gt;", SnipDoText.Resolve("{HTMLENCODED_TEXT}", "<x>"));
    }

    [Fact]
    public void MixedLongAndShortPlaceholdersResolveIdentically()
    {
        var resolved = SnipDoText.Resolve(
            "{PANTHERBAR_PLAIN_TEXT}|{PLAIN_TEXT}|{PANTHERBAR_URLENCODED_TEXT}|{URLENCODED_TEXT}",
            "a b");

        Assert.Equal("a b|a b|a+b|a+b", resolved);
    }

    [Fact]
    public void LeavesUnknownPlaceholderUntouched()
    {
        Assert.Equal("{APPOPTION missing}", SnipDoText.Resolve("{APPOPTION missing}", "x"));
    }

    // ---- T-14: replacement values are never rescanned --------------------

    [Fact]
    public void PlaceholderTextInTheClipboardIsNotExpandedASecondTime()
    {
        // The user copies the literal string "{PLAIN_TEXT}{URLENCODED_TEXT}". The template has
        // exactly one placeholder, so the output must contain those braces verbatim — a chain of
        // Replace() calls would have expanded the inserted text and pasted the clip twice.
        var clip = "{PLAIN_TEXT}{URLENCODED_TEXT}";

        Assert.Equal("[" + clip + "]", SnipDoText.Resolve("[{PLAIN_TEXT}]", clip));
        Assert.Equal(clip, SnipDoText.Resolve("{PANTHERBAR_PLAIN_TEXT}", clip));
    }

    [Fact]
    public void LongPlaceholderInsideClipTextIsAlsoPreserved()
    {
        var clip = "{PANTHERBAR_PLAIN_TEXT}";

        Assert.Equal("x" + clip + "y", SnipDoText.Resolve("x{PANTHERBAR_PLAIN_TEXT}y", clip));
        Assert.Equal(clip, SnipDoText.Resolve("{PANTHERBAR_PLAIN_TEXT}", clip));
    }

    [Fact]
    public void AppOptionValueContainingAPlaceholderIsNotExpanded()
    {
        var resolved = SnipDoText.Resolve(
            "https://{APPOPTION host}/",
            "ignored",
            new Dictionary<string, string> { ["host"] = "{PLAIN_TEXT}" });

        Assert.Equal("https://{PLAIN_TEXT}/", resolved);
    }

    [Fact]
    public void UnmatchedBraceDoesNotSwallowTheRestOfTheTemplate()
    {
        Assert.Equal("{ not a placeholder", SnipDoText.Resolve("{ not a placeholder", "x"));
        Assert.Equal("a{b{c", SnipDoText.Resolve("a{b{c", "x"));

        // A stray "{}" is not a placeholder, so it survives verbatim — and scanning must resume
        // right after it, not skip ahead to the next known token's closing brace.
        Assert.Equal("{}xok", SnipDoText.Resolve("{}{PLAIN_TEXT}ok", "x"));
    }

    [Fact]
    public void FindsUrlInsideText()
    {
        Assert.Equal("https://example.com/a", SnipDoText.FindUrl("看这个 https://example.com/a 很好"));
        Assert.Null(SnipDoText.FindUrl("没有链接"));
    }

    [Fact]
    public void DetectsUrlAndEmail()
    {
        Assert.True(SnipDoText.IsUrl("https://example.com"));
        Assert.True(SnipDoText.IsUrl("www.example.com"));
        Assert.False(SnipDoText.IsUrl("example.com"));
        Assert.True(SnipDoText.IsEmail("a.b@example.com"));
        Assert.False(SnipDoText.IsEmail("not an email"));
    }

    [Fact]
    public void DetectsFilePathViaCallback()
    {
        var existing = new HashSet<string>(["C:\\a.txt"]);
        Assert.True(SnipDoText.IsFilePath("C:\\a.txt", existing.Contains));
        Assert.True(SnipDoText.IsFilePath("\"C:\\a.txt\"", existing.Contains));
        Assert.False(SnipDoText.IsFilePath("C:\\b.txt", existing.Contains));
        Assert.False(SnipDoText.IsFilePath("two\nlines", existing.Contains));
    }
}

public class SnipDoActionMatcherTests
{
    private static SnipDoExtension ExtensionWith(SnipDoAction action) => new()
    {
        Name = "E",
        Identifier = "e",
        Actions = [action],
        RootPath = "C:\\ext",
    };

    [Fact]
    public void UrlActionVisibleForPlainText()
    {
        var action = new SnipDoAction { Title = "Go", Url = "https://x" };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("hello")));
    }

    [Fact]
    public void IsUrlRequirementFiltersNonUrls()
    {
        var action = new SnipDoAction { Title = "Open", Url = "u", Requirements = ["IS_URL"] };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("https://example.com")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("just text")));
    }

    [Fact]
    public void EmailRequirementFiltersNonEmails()
    {
        var action = new SnipDoAction { Title = "Mail", Url = "u", Requirements = ["EMAIL"] };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("a@b.com")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("text")));
    }

    [Fact]
    public void HasUrlRequirementMatchesUrlInsideText()
    {
        var action = new SnipDoAction { Title = "Go", Url = "u", Requirements = ["HAS_URL"] };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("see https://x.com now")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("no link")));
    }

    [Fact]
    public void FilePathRequirementUsesClipFiles()
    {
        var action = new SnipDoAction { Title = "Open", Url = "u", Requirements = ["FILEPATH"] };
        Assert.True(SnipDoActionMatcher.IsVisible(
            action,
            new ActionContext(string.Empty, ["C:\\a.txt"])));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("plain")));
    }

    [Fact]
    public void RegexGatesVisibility()
    {
        var action = new SnipDoAction { Title = "Digits", Url = "u", Regex = "^\\d+$" };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("12345")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("abc")));
    }

    [Fact]
    public void MalformedRegexDoesNotHideAction()
    {
        var action = new SnipDoAction { Title = "Bad", Url = "u", Regex = "([unclosed" };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("anything")));
    }

    [Fact]
    public void NoShowAppsHidesActionInThatProcess()
    {
        var action = new SnipDoAction { Title = "T", Url = "u", NoShowApps = ["Code.exe"] };
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("x", null, "Code")));
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("x", null, "Notepad")));
    }

    [Fact]
    public void ShowOnlyAppsLimitsActionToThoseProcesses()
    {
        var action = new SnipDoAction { Title = "T", Url = "u", ShowOnlyApps = ["chrome.exe"] };
        Assert.True(SnipDoActionMatcher.IsVisible(action, new ActionContext("x", null, "chrome")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("x", null, "Notepad")));
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("x")));
    }

    [Fact]
    public void ActionWithoutTargetIsNeverVisible()
    {
        var action = new SnipDoAction { Title = "Empty" };
        Assert.False(SnipDoActionMatcher.IsVisible(action, new ActionContext("x")));
    }

    [Fact]
    public void VisibleActionsWalksAllExtensions()
    {
        var extensions = new List<SnipDoExtension>
        {
            ExtensionWith(new SnipDoAction { Title = "A", Url = "https://a" }),
            ExtensionWith(new SnipDoAction { Title = "B", Url = "https://b", Requirements = ["IS_URL"] }),
        };

        var visible = SnipDoActionMatcher.VisibleActions(extensions, new ActionContext("plain text"));

        Assert.Equal(new[] { "A" }, visible.Select(a => a.Title));
    }
}
