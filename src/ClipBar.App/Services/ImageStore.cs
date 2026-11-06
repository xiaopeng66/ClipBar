using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.Core.Clipboard;

namespace ClipBar.App.Services;

public static class ImageStore
{
    public const int MaxDimension = 2560;

    public static async Task<SavedImage> SavePngAsync(
        BitmapSource source,
        string imagesDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!source.IsFrozen && source.CanFreeze)
        {
            source.Freeze();
        }

        BitmapSource output = source;
        if (source.PixelWidth > MaxDimension || source.PixelHeight > MaxDimension)
        {
            var scale = Math.Min(
                MaxDimension / (double)source.PixelWidth,
                MaxDimension / (double)source.PixelHeight);
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            output = transformed;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        await using var buffer = new MemoryStream();

        // Stream straight from the memory stream's own buffer. ToArray() copied the entire
        // encoded image a second time, which for a 2560px PNG is tens of megabytes of garbage
        // per capture on a machine that is also holding the decoded bitmap.
        encoder.Save(buffer);
        if (!buffer.TryGetBuffer(out var segment))
        {
            // Only possible for a non-expandable stream, which this never is; be explicit
            // rather than silently falling back to a second copy.
            throw new InvalidOperationException("PNG buffer was not readable in place.");
        }

        var length = checked((int)buffer.Length);
        var bytes = segment.AsMemory(0, length);
        var hash = ClipHash.ComputeImage(segment.AsSpan(0, length));

        Directory.CreateDirectory(imagesDirectory);
        var path = Path.Combine(imagesDirectory, hash + ".png");

        // F-04: the PNG is written to a unique temp file and atomically moved into place.
        // Writing straight to the final path used to leave a truncated file behind when a
        // cancellation or failure hit mid-write, and every later save of the same image then
        // skipped rewriting because the (broken) file existed.
        var temp = Path.Combine(imagesDirectory, ".tmp-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            var existing = new FileInfo(path);
            if (existing is { Exists: true, Length: var existingLength } && existingLength == length)
            {
                // The identical file is already complete (another writer won the race, or the
                // image was stored before); drop the redundant temp copy.
                File.Delete(temp);
            }
            else
            {
                // Either no file yet, or a damaged one (0 bytes, truncated). Same-volume
                // replace keeps the swap atomic: readers see the old file or the full one.
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Cancellation, a full disk, an IO error: never leave the half-written temp behind.
            TryDelete(temp);
            throw;
        }

        return new SavedImage(path, output.PixelWidth, output.PixelHeight, length, hash);
    }

    /// <summary>Copies a stored PNG to a user-chosen path (A4). Never re-encodes.</summary>
    public static bool SaveAs(string sourcePath, string targetPath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a deleted clip's PNG into images/trash so the 撤销 toast can bring it back (A6).
    /// Returns the trash path, or null when there is no file to preserve.
    /// </summary>
    public static string? MoveToTrash(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !Directory.Exists(Path.GetDirectoryName(imagePath)))
        {
            return null;
        }

        try
        {
            if (!File.Exists(imagePath))
            {
                return null;
            }

            var trash = Path.Combine(Path.GetDirectoryName(imagePath)!, "trash");
            Directory.CreateDirectory(trash);
            var target = Path.Combine(trash, Path.GetFileName(imagePath));
            File.Move(imagePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves a trashed PNG back beside the other images (undo).</summary>
    public static bool RestoreFromTrash(string trashPath, string imagesDirectory)
    {
        try
        {
            if (!File.Exists(trashPath))
            {
                return false;
            }

            Directory.CreateDirectory(imagesDirectory);
            File.Move(trashPath, Path.Combine(imagesDirectory, Path.GetFileName(trashPath)), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void DeleteTrashEntry(string? trashPath) => TryDelete(trashPath);

    /// <summary>Removes trash files older than <paramref name="maxAge"/> (startup sweep, A6).</summary>
    public static int SweepTrash(string imagesDirectory, TimeSpan maxAge)
    {
        var trash = Path.Combine(imagesDirectory, "trash");
        if (!Directory.Exists(trash))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(trash))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes PNGs in <paramref name="imagesDirectory"/> that no row references. A crash between
    /// writing the file and inserting the row leaves such an orphan, and nothing else ever
    /// collects them, so the folder would grow without bound. Only files this store would have
    /// created are considered — the file name is the <see cref="ClipHash"/> hex digest, so 64
    /// characters (SHA-256); 32-char leftovers from very old builds are collected too. A
    /// ".tmp-*" temp older than an hour (hard-kill mid-write) is collected as well; anything
    /// else — a stray user file, a fresh temp from a save in flight — is left alone.
    /// </summary>
    public static int SweepOrphans(string imagesDirectory, IEnumerable<string> referencedPaths)
    {
        if (!Directory.Exists(imagesDirectory))
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in referencedPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                referenced.Add(Path.GetFullPath(path));
            }
        }

        var removed = 0;
        // A hard kill mid-write (no exception to clean up with) can strand a ".tmp-*" file
        // forever. At sweep time no save is in flight, but an age floor keeps the rule
        // obviously safe: nothing legitimate keeps a temp for an hour.
        var staleTempCutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var file in Directory.EnumerateFiles(imagesDirectory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith(".tmp-", StringComparison.Ordinal))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < staleTempCutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                continue;
            }

            if (name.Length is not (32 or 64) || !name.All(Uri.IsHexDigit))
            {
                continue;
            }

            // Mid-write temps from SavePngAsync carry a ".tmp-" prefix (so they never look
            // like content), but a plain hex name that matches the hash contract is fair game.
            if (referenced.Contains(Path.GetFullPath(file)))
            {
                continue;
            }

            if (referenced.Contains(Path.GetFullPath(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}

public readonly record struct SavedImage(
    string Path,
    int Width,
    int Height,
    long SizeBytes,
    string Hash);
