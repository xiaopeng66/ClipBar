using System.IO;
namespace ClipBar.App;

public static class AppLog
{
    /// <summary>Rotate once the log passes this size; the old file becomes app.log.1.</summary>
    private const long MaxBytes = 1L * 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _directoryReady;

    public static void Write(string message)
    {
        try
        {
            // EnsureCreated() walks the whole data tree; doing it per line cost an IO hit on
            // every log entry. Once per process is enough.
            if (!_directoryReady)
            {
                AppPaths.EnsureCreated();
                _directoryReady = true;
            }

            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    AppPaths.LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down — but a data directory that cannot be written
            // (bad --data-dir, read-only volume) must not swallow the diagnostics either (F-05).
            WriteFallback(message);
        }
    }

    private static void WriteFallback(string message)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "ClipBar");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "app.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Error(string context, Exception exception)
        => Write($"ERROR {context}: {exception}");

    /// <summary>Keeps one previous file so a long-running install cannot fill the disk.</summary>
    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var previous = AppPaths.LogPath + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(AppPaths.LogPath, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
