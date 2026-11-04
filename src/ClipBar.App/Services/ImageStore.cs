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
        if (!File.Exists(path))
        {
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Lost the race to another writer of the same content (the filename is the
                // hash); its bytes are identical, so there is nothing left to do.
            }
        }

        return new SavedImage(path, output.PixelWidth, output.PixelHeight, length, hash);
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
    /// created (32 hex chars + .png) are considered, so a stray user file is left alone.
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
        foreach (var file in Directory.EnumerateFiles(imagesDirectory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != 32 || !name.All(Uri.IsHexDigit))
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
