using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClipBar.Core.Extensions;

/// <summary>
/// Writes the SnipDo plugin bundle to disk: an extension package (for importing into SnipDo)
/// and a paste-action folder (for SnipDo's clipboard bar), plus a .pbar zip of the package.
/// </summary>
public static class SnipDoPluginPackage
{
    public const string PackageFileName = "ClipBar.pbar";

    // The manifests carry Chinese translations, so they are UTF-8 without a BOM: PowerShell
    // 5.1 would misread a BOM-prefixed script, and neither SnipDo's nor our JSON reader needs
    // one.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes the bundle under <paramref name="rootDirectory"/> and returns its paths.</summary>
    public static SnipDoPluginFiles Write(string rootDirectory, string executablePath)
    {
        var extensionFolder = Path.Combine(rootDirectory, "extension");
        var pasteActionFolder = Path.Combine(rootDirectory, "paste-action");
        ResetDirectory(extensionFolder);
        ResetDirectory(pasteActionFolder);

        // The extension action opens the full panel; the paste action opens the compact list,
        // which is what SnipDo's own clipboard button shows.
        var panelLauncher = SnipDoPluginBuilder.LauncherScript(executablePath);
        var compactLauncher = SnipDoPluginBuilder.LauncherScript(executablePath, compact: true);

        // The extension copy is a normal SnipDo package: extension.json + script + icons.
        WriteText(Path.Combine(extensionFolder, "extension.json"), SnipDoPluginBuilder.ExtensionManifest());
        WriteText(Path.Combine(extensionFolder, SnipDoPluginBuilder.LauncherFileName), panelLauncher);
        WriteIcons(extensionFolder);

        // The paste-action copy uses data.json, the layout SnipDo's user store expects.
        WriteText(Path.Combine(pasteActionFolder, "data.json"), SnipDoPluginBuilder.PasteActionManifest());
        WriteText(Path.Combine(pasteActionFolder, SnipDoPluginBuilder.LauncherFileName), compactLauncher);
        WriteIcons(pasteActionFolder);

        var packagePath = Path.Combine(rootDirectory, PackageFileName);
        WritePackage(extensionFolder, packagePath);

        return new SnipDoPluginFiles(packagePath, extensionFolder, pasteActionFolder);
    }

    /// <summary>Copies the paste-action folder into a SnipDo store and returns the installed path.</summary>
    public static string InstallPasteAction(string pasteActionFolder, string storeDirectory)
    {
        var target = Path.Combine(storeDirectory, SnipDoPluginBuilder.PasteActionFolderName);
        ResetDirectory(target);
        foreach (var file in Directory.EnumerateFiles(pasteActionFolder))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return target;
    }

    public static bool IsPasteActionInstalled(string? storeDirectory)
        => storeDirectory is not null
            && Directory.Exists(Path.Combine(storeDirectory, SnipDoPluginBuilder.PasteActionFolderName));

    private static void WriteIcons(string folder)
    {
        WriteText(Path.Combine(folder, "logo.svg"), SnipDoPluginBuilder.MonochromeIcon());
        WriteText(Path.Combine(folder, "logo_color.svg"), SnipDoPluginBuilder.ColorIcon());
    }

    private static void WritePackage(string sourceFolder, string packagePath)
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(sourceFolder))
        {
            var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
            using var target = entry.Open();
            using var source = File.OpenRead(file);
            source.CopyTo(target);
        }
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }
}

/// <summary>Paths of a generated SnipDo plugin bundle.</summary>
public sealed record SnipDoPluginFiles(string PackagePath, string ExtensionFolder, string PasteActionFolder);
