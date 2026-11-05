using System.IO;
namespace ClipBar.App;

public static class AppPaths
{
    /// <summary>
    /// The data directory used by the pre-rename build. The layout is identical, so the first
    /// run of ClipBar moves it wholesale — the user keeps their history, settings, extensions
    /// and log (rename round, docs/RENAME-PLAN.md).
    /// </summary>
    private static string LegacyDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipboard11");

    private const string DefaultFolderName = "ClipBar";

    /// <summary>
    /// Data location. Defaults to %LOCALAPPDATA%\ClipBar; <see cref="Initialize"/> redirects it
    /// for --data-dir and portable copies (C3). Not readonly so the redirect is possible, but it
    /// must be set before any other member is used — OnStartup does that first.
    /// </summary>
    public static string DataDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DefaultFolderName);

    /// <summary>True while the default location is used, i.e. the legacy-directory migration applies.</summary>
    public static bool IsDefaultLocation { get; private set; } = true;

    /// <summary>
    /// Suffix for the single-instance mutex and show-event names: empty for the default location
    /// (SnipDo's launcher relies on the plain names), a short stable hash for custom directories so
    /// two portable copies can run side by side.
    /// </summary>
    public static string InstanceSuffix { get; private set; } = string.Empty;

    /// <summary>
    /// Chooses the data directory: an explicit --data-dir wins, then an exe-adjacent
    /// portable.marker (data goes to .\data), then the default location. Call once, first thing.
    /// </summary>
    public static void Initialize(string? dataDirectory, string? executableDirectory = null)
    {
        var target = dataDirectory;
        if (string.IsNullOrWhiteSpace(target)
            && !string.IsNullOrWhiteSpace(executableDirectory)
            && File.Exists(Path.Combine(executableDirectory, "portable.marker")))
        {
            target = Path.Combine(executableDirectory, "data");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        DataDirectory = Path.GetFullPath(target);
        IsDefaultLocation = false;
        InstanceSuffix = InstanceSuffixFor(DataDirectory);
    }

    /// <summary>Stable 8-hex suffix so two copies with different folders never collide.</summary>
    public static string InstanceSuffixFor(string directory)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToLowerInvariant()));
        return "." + Convert.ToHexString(bytes)[..8];
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "history.db");

    public static string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string ExtensionsDirectory => Path.Combine(DataDirectory, "extensions");

    public static string LogPath => Path.Combine(DataDirectory, "app.log");

    public static void EnsureCreated()
    {
        MigrateLegacyDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(ExtensionsDirectory);
    }

    private static void MigrateLegacyDataDirectory()
    {
        // Only the default location inherits the pre-rename folder; a custom/portable directory
        // must start empty rather than silently stealing %LOCALAPPDATA%\Clipboard11.
        if (!IsDefaultLocation || Directory.Exists(DataDirectory) || !Directory.Exists(LegacyDataDirectory))
        {
            return;
        }

        try
        {
            // Same volume: the move is instant and keeps every file (db, settings, images,
            // extensions, log, SnipDo export). Nothing is deleted — if the move fails the
            // original stays untouched and ClipBar simply starts fresh.
            Directory.Move(LegacyDataDirectory, DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopyDirectory(LegacyDataDirectory, DataDirectory);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}