using System.IO;
namespace ClipBar.App;

public static class AppPaths
{
    /// <summary>
    /// The data directory used by the pre-rename build. The layout is identical, so the first
    /// run of ClipBar moves it wholesale — the user keeps their history, settings, images
    /// and log (rename round, docs/RENAME-PLAN.md).
    /// </summary>
    private static string LegacyDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipboard11");

    private const string DefaultFolderName = "ClipBar";

    private static string DefaultDataDirectory { get; } = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DefaultFolderName));

    /// <summary>
    /// Data location. Defaults to %LOCALAPPDATA%\ClipBar; <see cref="Initialize"/> redirects it
    /// for --data-dir and portable copies (C3). Not readonly so the redirect is possible, but it
    /// must be set before any other member is used — OnStartup does that first.
    /// </summary>
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    /// <summary>True while the default location is used, i.e. the legacy-directory migration applies.</summary>
    public static bool IsDefaultLocation { get; private set; } = true;

    /// <summary>
    /// Suffix for the single-instance mutex and show-event names: empty for the default location
    /// (external launchers rely on the plain names), a short stable hash for custom directories
    /// so two portable copies can run side by side.
    /// </summary>
    public static string InstanceSuffix { get; private set; } = string.Empty;

    /// <summary>Set when a requested data directory was unusable and the default was kept.</summary>
    public static string? InitializationWarning { get; private set; }

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

        string fullPath;
        try
        {
            fullPath = NormalizeDataDirectory(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            InitializationWarning = $"数据目录路径无效（{target}）：{ex.Message}，已回退到默认目录。";
            return;
        }

        // A custom directory that cannot be created or written would otherwise fail every later
        // step (settings, DB, log) with no diagnostics — validate it once, up front (F-05). For
        // an explicit default path, migrate legacy data before the probe creates an empty target.
        if (!TryPrepareDataDirectory(fullPath, DefaultDataDirectory, LegacyDataDirectory, out var error))
        {
            InitializationWarning = $"数据目录不可用（{fullPath}）：{error}，已回退到默认目录。";
            return;
        }

        DataDirectory = fullPath;
        IsDefaultLocation = IsDefaultDataDirectory(fullPath);
        InstanceSuffix = IsDefaultLocation ? string.Empty : InstanceSuffixFor(fullPath);
    }

    /// <summary>Runs the default-profile migration before creating the writable-path probe.</summary>
    internal static bool TryPrepareDataDirectory(
        string directory,
        string defaultDirectory,
        string legacyDirectory,
        out string error)
    {
        var isDefault = string.Equals(
            NormalizeDataDirectory(directory),
            NormalizeDataDirectory(defaultDirectory),
            StringComparison.OrdinalIgnoreCase);
        if (isDefault)
        {
            MigrateLegacyDataDirectory(directory, legacyDirectory, isDefaultLocation: true);
        }

        return TryPrepare(directory, out error);
    }

    /// <summary>Creates the directory and proves it is writable with a probe file.</summary>
    private static bool TryPrepare(string directory, out string error)
    {
        string? probe = null;
        var probeCreated = false;
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                probeCreated = true;
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probe);
            probeCreated = false;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (probeCreated && probe is not null)
            {
                try
                {
                    File.Delete(probe);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Stable 8-hex suffix so two copies with different folders never collide.</summary>
    public static string InstanceSuffixFor(string directory)
    {
        var normalized = NormalizeDataDirectory(directory);
        if (string.Equals(normalized, NormalizeDataDirectory(DefaultDataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return "." + Convert.ToHexString(bytes)[..8];
    }

    private static bool IsDefaultDataDirectory(string directory)
        => string.Equals(
            NormalizeDataDirectory(directory),
            NormalizeDataDirectory(DefaultDataDirectory),
            StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeDataDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "history.db");

    public static string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogPath => Path.Combine(DataDirectory, "app.log");

    public static void EnsureCreated()
    {
        MigrateLegacyDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ImagesDirectory);
    }

    private static void MigrateLegacyDataDirectory()
        => MigrateLegacyDataDirectory(DataDirectory, LegacyDataDirectory, IsDefaultLocation);

    internal static void MigrateLegacyDataDirectory(
        string dataDirectory,
        string legacyDataDirectory,
        bool isDefaultLocation)
    {
        // Only the default location inherits the pre-rename folder; a custom/portable directory
        // must start empty rather than silently stealing %LOCALAPPDATA%\Clipboard11.
        if (!isDefaultLocation || Directory.Exists(dataDirectory) || !Directory.Exists(legacyDataDirectory))
        {
            return;
        }

        try
        {
            // Same volume: the move is instant and keeps every file (db, settings, images,
            // log, plus any leftovers from an older build). Nothing is deleted — if the move
            // fails the original stays untouched and ClipBar simply starts fresh.
            Directory.Move(legacyDataDirectory, dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                CopyDirectory(legacyDataDirectory, dataDirectory);
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(legacyDataDirectory))
            {
                // A parallel launch may have moved the legacy directory after our initial check.
                // Its target now contains the migrated data, so the losing launch can continue.
            }
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