using System.Text.Json;
using ClipBar.Core.Models;

namespace ClipBar.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    /// <summary>
    /// Set when the last <see cref="Load"/> had to fall back to defaults, so the caller can tell
    /// the user their settings were not applied instead of silently resetting them.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        LastLoadWarning = null;

        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                // A file holding just "null" is corrupt, not an empty configuration. Returning
                // defaults here would let the next Save silently overwrite the user's settings.
                throw new JsonException("设置文件的内容不是一个设置对象。");
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A truncated or hand-edited file used to be replaced by defaults on the next save,
            // so the user's configuration vanished without a word. Keep the bad file for
            // inspection, start from defaults, and say so.
            var backup = Quarantine();
            LastLoadWarning = backup is null
                ? "设置文件无法读取，已使用默认设置：" + ex.Message
                : "设置文件已损坏，已改用默认设置；原文件保留为 " + Path.GetFileName(backup);
            return new AppSettings();
        }
    }

    private string? Quarantine()
    {
        try
        {
            var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(_path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        // A fixed ".tmp" name collides when two instances write at once, and a plain
        // File.WriteAllText leaves the rename visible before the bytes reach the disk, so a
        // power cut could leave an empty settings file. Use a unique temp name and flush it
        // through the OS cache before swapping.
        var temp = Path.Combine(directory, Path.GetRandomFileName() + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(JsonSerializer.Serialize(settings, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
