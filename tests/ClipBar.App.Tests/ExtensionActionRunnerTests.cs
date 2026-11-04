using System.IO;
using ClipBar.App.Services;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.1 T-07..T-10: hostile script paths and URL schemes. Everything goes through the
/// public <see cref="ExtensionActionRunner.RunAsync"/> entry point, because the path and scheme
/// checks are private static helpers — the only honest way to prove them is to observe what the
/// runner actually does.
/// </summary>
public class ExtensionActionRunnerTests
{
    private const string UnsafeUrlPrefix = "这个扩展想打开不支持的链接类型：";
    private const string MissingScriptPrefix = "找不到扩展脚本：";

    private static ExtensionActionRunner NewRunner() => new(() => IntPtr.Zero);

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "clipbar-run-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteScript(string relativeName, string body)
        {
            var path = Path.Combine(Root, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body, System.Text.Encoding.UTF8);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static SnipDoAction PowerShellAction(string? script, string scriptRoot) => new()
    {
        Title = "Run script",
        PowershellFile = script,
        ScriptRoot = scriptRoot,
    };

    // ---- T-07: absolute script path from the manifest --------------------

    [Fact]
    public async Task AbsoluteScriptPathIsRefusedWithoutAPackageRoot()
    {
        // No ScriptRoot means the runner has no trusted directory to confine the script to, so
        // an absolute path can only come from the manifest itself — it must never execute.
        var result = await NewRunner().RunAsync(
            PowerShellAction(@"C:\Windows\System32\WindowsPowerShell\v1.0\profile.ps1", string.Empty),
            "clip text",
            null,
            null);

        Assert.False(result.Success);
        Assert.StartsWith(MissingScriptPrefix, result.Error);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task AbsoluteScriptPathIsRefusedEvenWithAPackageRoot()
    {
        using var workspace = new Workspace();
        var result = await NewRunner().RunAsync(
            PowerShellAction(@"C:\Windows\System32\calc.exe", workspace.Root),
            "clip text",
            null,
            null);

        Assert.False(result.Success);
        Assert.StartsWith(MissingScriptPrefix, result.Error);
    }

    // ---- T-08: traversal out of the package ------------------------------

    [Theory]
    [InlineData(@"..\..\..\..\x.ps1")]
    [InlineData("../../../../x.ps1")]
    [InlineData(@"..\x.ps1")]
    [InlineData("sub/../../x.ps1")]
    public async Task ScriptEscapingThePackageRootIsRefused(string relative)
    {
        using var workspace = new Workspace();
        // The target exists one level above the package root, so only the containment check can
        // stop it — File.Exists alone would happily resolve it.
        var outside = Path.Combine(Path.GetDirectoryName(workspace.Root)!, "x.ps1");
        File.WriteAllText(outside, "Write-Output 'pwned'", System.Text.Encoding.UTF8);

        try
        {
            var result = await NewRunner().RunAsync(
                PowerShellAction(relative, workspace.Root),
                "clip text",
                null,
                null);

            Assert.False(result.Success);
            Assert.StartsWith(MissingScriptPrefix, result.Error);
        }
        finally
        {
            try
            {
                File.Delete(outside);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- positive control: a legitimate in-package script does run -------

    [Fact]
    public async Task ScriptInsideThePackageRootRuns()
    {
        using var workspace = new Workspace();
        workspace.WriteScript("scripts/hello.ps1", "Write-Output 'hello-from-extension'");

        var result = await NewRunner().RunAsync(
            PowerShellAction(@"scripts\hello.ps1", workspace.Root),
            "clip text",
            null,
            null);

        Assert.True(result.Success, result.Error);
        Assert.Contains("hello-from-extension", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FailingScriptReportsItsExitCodeInsteadOfSucceeding()
    {
        using var workspace = new Workspace();
        workspace.WriteScript("fail.ps1", "exit 3");

        var result = await NewRunner().RunAsync(
            PowerShellAction("fail.ps1", workspace.Root),
            "clip text",
            null,
            null);

        Assert.False(result.Success);
        Assert.Contains("3", result.Error);
    }

    [Fact]
    public async Task MissingScriptInsideTheRootIsReportedNotExecuted()
    {
        using var workspace = new Workspace();
        var result = await NewRunner().RunAsync(
            PowerShellAction("nope.ps1", workspace.Root),
            "clip text",
            null,
            null);

        Assert.False(result.Success);
        Assert.StartsWith(MissingScriptPrefix, result.Error);
    }

    // ---- T-09 / T-10: dangerous schemes built from clipboard text --------

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("ms-msdt:/id")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com/x")]
    [InlineData("vbscript:msgbox(1)")]
    public async Task UrlPlaceholderCannotSmuggleADangerousScheme(string clipText)
    {
        // The manifest only says "{PLAIN_TEXT}"; the scheme comes entirely from whatever the
        // user copied. This is the exact shape a malicious extension would ship.
        var action = new SnipDoAction
        {
            Title = "Open",
            Url = "{PLAIN_TEXT}",
            ScriptRoot = string.Empty,
        };

        var result = await NewRunner().RunAsync(action, clipText, null, null);

        Assert.False(result.Success);
        Assert.StartsWith(UnsafeUrlPrefix, result.Error);
    }

    [Fact]
    public void WebUrlFromClipboardTextSurvivesTheGate()
    {
        // Deliberately stops short of RunAsync: a passing URL really does launch the default
        // browser, and a unit test must not open windows on the user's desktop.
        var action = new SnipDoAction
        {
            Title = "Search",
            Url = "https://example.com/s?q={URLENCODED_TEXT}",
            ScriptRoot = string.Empty,
        };

        var resolved = ClipBar.Core.Extensions.SnipDoText.Resolve(action.Url!, "a b");
        Assert.Equal("https://example.com/s?q=a+b", resolved);
        Assert.Equal(resolved, ClipBar.Core.Security.SafeUrl.Normalize(resolved));
    }

    // ---- kind dispatch --------------------------------------------------

    [Fact]
    public async Task ActionWithNoExecutableKindFailsCleanly()
    {
        var action = new SnipDoAction { Title = "Nothing", ScriptRoot = string.Empty };

        var result = await NewRunner().RunAsync(action, "x", null, null);

        Assert.False(result.Success);
        Assert.Equal(SnipDoActionKind.None, action.Kind);
    }

    [Fact]
    public void KindPrefersUrlThenScriptsThenKeys()
    {
        Assert.Equal(SnipDoActionKind.Url, new SnipDoAction { Url = "https://x" }.Kind);
        Assert.Equal(SnipDoActionKind.PowerShell, new SnipDoAction { PowershellFile = "a.ps1" }.Kind);
        Assert.Equal(SnipDoActionKind.Python, new SnipDoAction { PythonFile = "a.py" }.Kind);
        Assert.Equal(SnipDoActionKind.KeyCombo, new SnipDoAction { KeyCombo = "Ctrl+C" }.Kind);
    }
}
