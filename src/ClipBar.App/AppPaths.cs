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

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipBar");

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
        if (Directory.Exists(DataDirectory) || !Directory.Exists(LegacyDataDirectory))
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