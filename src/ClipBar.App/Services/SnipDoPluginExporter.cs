using System.IO;
using ClipBar.Core.Extensions;

namespace ClipBar.App.Services;

/// <summary>
/// Builds the SnipDo-side half of the integration. SnipDo's clipboard bar is a "paste action"
/// (<c>BuildInPasteActions\0PasteMain\data.json</c>), so the plugin ships one of those plus a
/// normal extension whose script summons the ClipBar panel. That way ClipBar replaces
/// SnipDo's clipboard list, instead of importing it.
/// </summary>
public sealed class SnipDoPluginExporter
{
    private readonly string _outputDirectory;
    private readonly Func<string> _executablePath;

    public SnipDoPluginExporter(string? outputDirectory = null, Func<string>? executablePath = null)
    {
        _outputDirectory = outputDirectory ?? DefaultOutputDirectory;
        _executablePath = executablePath ?? (() => Environment.ProcessPath ?? string.Empty);
    }

    public static string DefaultOutputDirectory => Path.Combine(AppPaths.DataDirectory, "snipdo");

    public string OutputDirectory => _outputDirectory;

    public string PackagePath => Path.Combine(_outputDirectory, SnipDoPluginPackage.PackageFileName);

    /// <summary>Writes the unpacked plugin and rebuilds the .pbar package from it.</summary>
    public SnipDoPluginFiles Export()
    {
        Directory.CreateDirectory(_outputDirectory);
        return SnipDoPluginPackage.Write(_outputDirectory, _executablePath());
    }

    /// <summary>
    /// Exports the plugin and copies the paste action into SnipDo's user store, which is what
    /// makes the action show up in SnipDo's clipboard bar. Returns the installed folder.
    /// </summary>
    public string InstallPasteAction()
    {
        var plugin = Export();
        var store = FindPasteActionsDirectory(create: true)
            ?? throw new InvalidOperationException(
                "没有找到 SnipDo 的数据目录。请先安装并运行一次 SnipDo，或改用「导出 .pbar」在 SnipDo 里手动导入。");

        return SnipDoPluginPackage.InstallPasteAction(plugin.PasteActionFolder, store);
    }

    public static bool IsPasteActionInstalled()
        => SnipDoPluginPackage.IsPasteActionInstalled(FindPasteActionsDirectory());

    /// <summary>
    /// Locates SnipDo's user paste-action store. SnipDo ships as a Store app, so its data lives
    /// under <c>%LOCALAPPDATA%\Packages\JohannesTscholl.Pantherbar_*</c>; an unpackaged copy
    /// would use a plain <c>%LOCALAPPDATA%\SnipDo</c> layout instead.
    /// </summary>
    public static string? FindPasteActionsDirectory(bool create = false)
    {
        var roots = EnumerateSnipDoRoots().ToList();

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "PasteActions");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!create)
        {
            return null;
        }

        // SnipDo creates PasteActions on first run; if it has not, create it in the most
        // likely root (the packaged one, else the plain one).
        foreach (var root in roots)
        {
            var created = Path.Combine(root, "PasteActions");
            try
            {
                Directory.CreateDirectory(created);
                return created;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSnipDoRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            yield break;
        }

        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages))
            {
                var name = Path.GetFileName(package);
                if (!name.Contains("Pantherbar", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("SnipDo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Packaged apps keep their data under LocalCache\{Local,Roaming}; a sideloaded
                // or unpackaged copy uses Local / Roaming directly.
                foreach (var cache in new[] { "LocalCache\\Local", "LocalCache\\Roaming", "Local", "Roaming" })
                {
                    var root = Path.Combine(package, cache, "SnipDo");
                    if (Directory.Exists(root))
                    {
                        yield return root;
                    }
                }
            }
        }

        var plain = Path.Combine(localAppData, "SnipDo");
        if (Directory.Exists(plain))
        {
            yield return plain;
        }
    }
}
