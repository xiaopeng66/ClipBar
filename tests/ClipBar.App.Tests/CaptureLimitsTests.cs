using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.2 T-21 / T-22 / T-23 / T-29: the caps that keep one pathological clip from bloating
/// the database, and the content-addressed image store that keeps a repeated copy from bloating
/// the images folder. Everything writes under the temp directory.
/// </summary>
public class CaptureLimitsTests
{
    // ---- T-21: huge text ------------------------------------------------

    [Fact]
    public void TextExactlyAtTheCapIsNotTouched()
    {
        var text = new string('a', ClipboardMonitor.MaxTextLength);

        var (kept, truncated) = ClipboardMonitor.ApplyTextCap(text);

        Assert.False(truncated);
        Assert.Same(text, kept);
    }

    [Fact]
    public void TenMegabyteTextIsCutToTheCap()
    {
        // 10 MB of text: 10x the cap. The stored row must be exactly MaxTextLength characters.
        var text = new string('b', 10 * 1024 * 1024);

        var (kept, truncated) = ClipboardMonitor.ApplyTextCap(text);

        Assert.True(truncated);
        Assert.Equal(ClipboardMonitor.MaxTextLength, kept.Length);
        Assert.Equal(text[..ClipboardMonitor.MaxTextLength], kept);
    }

    [Fact]
    public void FiftyMegabyteTextIsCutToTheCapQuickly()
    {
        // 50 MB is the worst case a real clipboard can hand us (a whole log or dump file).
        // Slicing must be O(cap), not O(input): a full copy would double the memory for nothing.
        var text = new string('c', 50 * 1024 * 1024);

        var stopwatch = Stopwatch.StartNew();
        var (kept, truncated) = ClipboardMonitor.ApplyTextCap(text);
        stopwatch.Stop();

        Assert.True(truncated);
        Assert.Equal(ClipboardMonitor.MaxTextLength, kept.Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), $"截断耗时 {stopwatch.Elapsed}");
    }

    [Fact]
    public void EmptyTextIsNotFlaggedAsTruncated()
    {
        Assert.Equal((string.Empty, false), ClipboardMonitor.ApplyTextCap(string.Empty));
    }

    // ---- T-22: ten thousand file paths -----------------------------------

    [Fact]
    public void TenThousandFilePathsAreCutToTwoHundred()
    {
        var files = Enumerable.Range(0, 10_000)
            .Select(i => $@"C:\stress\file{i:D5}.txt")
            .ToList();

        var (kept, dropped) = ClipboardMonitor.ApplyFileCap(files);

        Assert.Equal(ClipboardMonitor.MaxFilePaths, kept.Count);
        Assert.Equal(10_000 - ClipboardMonitor.MaxFilePaths, dropped);
        Assert.Equal(files.Take(ClipboardMonitor.MaxFilePaths), kept);
    }

    [Fact]
    public void FilePathsUnderTheCapAreAllKept()
    {
        var files = Enumerable.Range(0, 3).Select(i => $@"C:\a\{i}.txt").ToList();

        var (kept, dropped) = ClipboardMonitor.ApplyFileCap(files);

        Assert.Equal(files, kept);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void CappedPathsSerializeToASmallJsonArray()
    {
        // What lands in the file_paths column. Before the cap a 10,000-file selection produced
        // a ~600 KB cell on a single row, which every query then had to read.
        var files = Enumerable.Range(0, 10_000)
            .Select(i => $@"C:\stress\file{i:D5}.txt")
            .ToList();

        var (kept, _) = ClipboardMonitor.ApplyFileCap(files);
        var json = System.Text.Json.JsonSerializer.Serialize(kept);
        var uncapped = System.Text.Json.JsonSerializer.Serialize(files);

        Assert.True(json.Length < 8192, $"file_paths 仍有 {json.Length} 字符");
        Assert.True(uncapped.Length > json.Length * 10);
    }

    // ---- T-23: oversized image -------------------------------------------

    [Fact]
    public async Task OversizedImageIsDownscaledToTheCap()
    {
        // 8000x8000 is the plan's worst case. Pixels are written one row at a time so the test
        // itself does not need a second full-size buffer.
        const int side = 8000;
        var source = SolidBitmap(side, side);

        var dir = NewDir();
        try
        {
            var saved = await ImageStore.SavePngAsync(source, dir);

            Assert.Equal(ImageStore.MaxDimension, Math.Max(saved.Width, saved.Height));
            Assert.Equal(ImageStore.MaxDimension, saved.Width);
            Assert.Equal(ImageStore.MaxDimension, saved.Height);

            // The PNG on disk must decode to the same downscaled size, not to 8000x8000.
            // OnLoad releases the file handle immediately, so the cleanup below can delete it.
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.UriSource = new Uri(saved.Path);
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.EndInit();
            Assert.Equal(ImageStore.MaxDimension, decoded.PixelWidth);
            Assert.Equal(ImageStore.MaxDimension, decoded.PixelHeight);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NonSquareImageKeepsItsAspectRatioWhenDownscaled()
    {
        var source = SolidBitmap(4000, 1000);

        var dir = NewDir();
        try
        {
            var saved = await ImageStore.SavePngAsync(source, dir);

            Assert.Equal(ImageStore.MaxDimension, saved.Width);
            Assert.Equal(640, saved.Height); // 1000 * (2560/4000)
            Assert.True(saved.SizeBytes > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImageAtTheCapIsNotResampled()
    {
        var source = SolidBitmap(ImageStore.MaxDimension, 100);

        var dir = NewDir();
        try
        {
            var saved = await ImageStore.SavePngAsync(source, dir);

            Assert.Equal(ImageStore.MaxDimension, saved.Width);
            Assert.Equal(100, saved.Height);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- T-29: the same image copied over and over -----------------------

    [Fact]
    public async Task SavingTheSameImageAHundredTimesLeavesExactlyOneFile()
    {
        var source = PatternBitmap(64, 64);

        var dir = NewDir();
        try
        {
            var first = await ImageStore.SavePngAsync(source, dir);

            for (var i = 1; i < 100; i++)
            {
                var again = await ImageStore.SavePngAsync(source, dir);
                Assert.Equal(first.Path, again.Path);
                Assert.Equal(first.Hash, again.Hash);
            }

            // The filename is the content hash, so a hundred copies of one image are one file.
            Assert.Single(Directory.GetFiles(dir, "*.png"));
            Assert.Equal(first.Path, Directory.GetFiles(dir, "*.png")[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSavesOfTheSameImageDoNotFailOrDuplicate()
    {
        // Two capture windows racing on the same image used to leave one writer with an
        // IOException. The store tolerates losing that race because the bytes are identical.
        var source = PatternBitmap(48, 48);

        var dir = NewDir();
        try
        {
            var results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => ImageStore.SavePngAsync(source, dir)));

            Assert.Single(results.Select(r => r.Hash).Distinct());
            Assert.Single(Directory.GetFiles(dir, "*.png"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DifferentImagesGetDifferentFiles()
    {
        var dir = NewDir();
        try
        {
            var a = await ImageStore.SavePngAsync(PatternBitmap(32, 32, seed: 1), dir);
            var b = await ImageStore.SavePngAsync(PatternBitmap(32, 32, seed: 2), dir);

            Assert.NotEqual(a.Hash, b.Hash);
            Assert.NotEqual(a.Path, b.Path);
            Assert.Equal(2, Directory.GetFiles(dir, "*.png").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- T-50: one unreadable path must not lose the whole clip -----------

    /// <summary>
    /// Plan §5.2 T-50. The contract under test is "one unreadable path must not lose the whole
    /// clip": the caller sums <c>SafeFileSize</c> over the entire selection
    /// (<c>SizeBytes = files.Sum(SafeFileSize)</c>), so anything escaping it would abort the sum,
    /// escape <c>BuildItemAsync</c>, and be swallowed by the outer <c>catch (Exception)</c> in
    /// <c>CaptureAsync</c> — dropping the clip instead of one size field.
    /// <para>
    /// Honest scope note: measurement showed <see cref="File.Exists"/> never throws (false for
    /// empty, embedded NUL, bare device prefix, nonexistent drive, null), so the hostile-payload
    /// variant of this bug is <b>not reachable</b> and this is hardening rather than a live defect.
    /// The cases below still pin the contract so a future refactor cannot silently reintroduce a
    /// throwing path — e.g. by switching to <c>new FileInfo(path).Length</c> without the guard.
    /// </para>
    /// </summary>
    [Fact]
    public void SafeFileSizeReportsTheLengthOfARealFile()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "sized.txt");
            File.WriteAllBytes(path, new byte[4096]);

            Assert.Equal(4096, ClipboardMonitor.SafeFileSize(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SafeFileSizeIsZeroForAPathThatDoesNotExist()
    {
        Assert.Equal(0, ClipboardMonitor.SafeFileSize(Path.Combine(Path.GetTempPath(), "clipbar-gone-" + Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("C:\\a\0b.txt")]          // embedded NUL: ArgumentException territory
    [InlineData("Q:\\nope\\nope.txt")]    // drive letter that does not exist
    [InlineData("::::")]
    [InlineData("\\\\?\\")]               // bare device prefix
    public void SafeFileSizeIsZeroForAMalformedPathAndDoesNotThrow(string? path)
    {
        // A hostile clipboard can hand us any of these. The contract is 0, never an exception.
        Assert.Equal(0, ClipboardMonitor.SafeFileSize(path!));
    }

    [Fact]
    public void SafeFileSizeIsZeroForADirectory()
    {
        // A shell selection can contain folders; CF_HDROP does not promise files only.
        Assert.Equal(0, ClipboardMonitor.SafeFileSize(Path.GetTempPath()));
    }

    [Fact]
    public void SummingAMixedSelectionDoesNotThrow()
    {
        // The actual regression: this is what BuildItemAsync does. Before the fix, the malformed
        // entries in the middle took the whole clip with them.
        var dir = NewDir();
        try
        {
            var good = Path.Combine(dir, "good.txt");
            File.WriteAllBytes(good, new byte[1000]);

            var selection = new[]
            {
                good,
                Path.Combine(dir, "deleted-between-copy-and-capture.txt"),
                "C:\\a\0b.txt",
                Path.GetTempPath(),
                "Q:\\nope\\nope.txt",
                null!,
            };

            var total = selection.Sum(path => ClipboardMonitor.SafeFileSize(path));

            Assert.Equal(1000, total);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbar-capture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static BitmapSource SolidBitmap(int width, int height)
    {
        // One row is written repeatedly: allocating width*height*4 at 8000x8000 would be a
        // second 244 MB buffer just to prove the store can resize one.
        var stride = width * 4;
        var row = new byte[stride];
        for (var x = 0; x < width; x++)
        {
            row[x * 4 + 0] = 0x20;
            row[x * 4 + 1] = 0x60;
            row[x * 4 + 2] = 0xC0;
            row[x * 4 + 3] = 0xFF;
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        for (var y = 0; y < height; y++)
        {
            bitmap.WritePixels(new System.Windows.Int32Rect(0, y, width, 1), row, stride, 0);
        }

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource PatternBitmap(int width, int height, int seed = 0)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                buffer[i + 0] = (byte)((x * 7 + seed) % 256);
                buffer[i + 1] = (byte)((y * 11 + seed) % 256);
                buffer[i + 2] = (byte)((x + y + seed) % 256);
                buffer[i + 3] = 0xFF;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), buffer, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
