using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClipBar.Core.Extensions;
using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

/// <summary>
/// Guards the plugin ClipBar generates for SnipDo: the extension manifest must survive our
/// own parser (so a re-import works), and the paste action must carry the fields SnipDo's
/// clipboard bar reads.
/// </summary>
public sealed class SnipDoPluginBuilderTests
{
    [Fact]
    public void ExtensionManifestParsesAsAPowerShellAction()
    {
        var extension = SnipDoManifestParser.Parse(SnipDoPluginBuilder.ExtensionManifest(), "C:\\plugin");

        Assert.Equal(SnipDoPluginBuilder.ExtensionIdentifier, extension.Identifier);
        var action = Assert.Single(extension.Actions);
        Assert.Equal(SnipDoActionKind.PowerShell, action.Kind);
        Assert.Equal(SnipDoPluginBuilder.LauncherFileName, action.PowershellFile);
        Assert.Equal("剪贴板历史", action.DisplayTitle);
        Assert.Equal("ClipBar 剪贴板", extension.DisplayName);
    }

    [Fact]
    public void PasteActionManifestDeclaresIdentifierScriptAndTranslations()
    {
        using var document = JsonDocument.Parse(SnipDoPluginBuilder.PasteActionManifest());
        var root = document.RootElement;

        Assert.Equal(
            SnipDoPluginBuilder.PasteActionIdentifier,
            root.GetProperty("identifier_pasteaction").GetString());
        Assert.Equal(SnipDoPluginBuilder.LauncherFileName, root.GetProperty("powershellFile").GetString());
        Assert.Equal("剪贴板", root.GetProperty("actionTranslations").GetProperty("zh").GetString());
        Assert.Equal("Clipboard", root.GetProperty("actionTranslations").GetProperty("en").GetString());
        Assert.Equal("logo.svg", root.GetProperty("icon").GetString());
        Assert.Equal("logo_color.svg", root.GetProperty("icon_colored").GetString());
    }

    [Fact]
    public void LauncherScriptIsAsciiAndSignalsTheRunningInstance()
    {
        var script = SnipDoPluginBuilder.LauncherScript(@"C:\Tools\ClipBar.exe");

        Assert.All(script, c => Assert.True(c < 128, $"non-ASCII character '{c}' in launcher script"));
        Assert.Contains(@"Local\ClipBar.Show", script, StringComparison.Ordinal);
        Assert.Contains("--show", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\Tools\ClipBar.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherScriptEscapesQuotesInTheExecutablePath()
    {
        var script = SnipDoPluginBuilder.LauncherScript(@"C:\It's here\ClipBar.exe");

        Assert.Contains(@"C:\It''s here\ClipBar.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IconsAreWellFormedSvgWithTwentyFourUnitViewBox()
    {
        foreach (var svg in new[] { SnipDoPluginBuilder.MonochromeIcon(), SnipDoPluginBuilder.ColorIcon() })
        {
            Assert.StartsWith("<svg", svg.TrimStart(), StringComparison.Ordinal);
            Assert.Contains("viewBox=\"0 0 24 24\"", svg, StringComparison.Ordinal);
            Assert.EndsWith("</svg>", svg.TrimEnd(), StringComparison.Ordinal);

            // Both icons must be loadable as text with no stray control characters.
            Assert.DoesNotContain('\uFEFF', svg);
            Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(svg)), svg);
        }
    }

    [Fact]
    public void PackageRoundTripsThroughTheImporter()
    {
        var root = Path.Combine(Path.GetTempPath(), "c11-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var files = SnipDoPluginPackage.Write(root, @"C:\Tools\ClipBar.exe");

            // The .pbar is a zip holding extension.json at its root, which is exactly the
            // layout ExtensionService.ImportAsync expects.
            using (var archive = ZipFile.OpenRead(files.PackagePath))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                Assert.Contains("extension.json", names);
                Assert.Contains(SnipDoPluginBuilder.LauncherFileName, names);
                Assert.Contains("logo.svg", names);
                Assert.Contains("logo_color.svg", names);
            }

            var manifest = File.ReadAllText(Path.Combine(files.ExtensionFolder, "extension.json"), Encoding.UTF8);
            var extension = SnipDoManifestParser.Parse(manifest, files.ExtensionFolder);
            Assert.Equal(SnipDoPluginBuilder.ExtensionIdentifier, extension.Identifier);
            Assert.NotNull(extension.IconPath);

            // The paste-action store copy carries data.json and no BOM on any text file.
            var pasteManifest = Path.Combine(files.PasteActionFolder, "data.json");
            Assert.True(File.Exists(pasteManifest));
            var bytes = File.ReadAllBytes(pasteManifest);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB, "data.json must not start with a UTF-8 BOM");

            var installed = SnipDoPluginPackage.InstallPasteAction(files.PasteActionFolder, root);
            Assert.True(Directory.Exists(installed));
            Assert.True(SnipDoPluginPackage.IsPasteActionInstalled(root));
            Assert.Equal(SnipDoPluginBuilder.PasteActionFolderName, Path.GetFileName(installed));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
