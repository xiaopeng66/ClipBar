using System.IO;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

public class ImageStoreTests
{
    private const string HexA = "0123456789abcdef0123456789abcdef";
    private const string HexB = "fedcba9876543210fedcba9876543210";

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbar-images-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveAsCopiesTheStoredPngToTheChosenPath()
    {
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, HexA + ".png");
            var target = Path.Combine(dir, "saved.png");
            File.WriteAllBytes(source, [1, 2, 3]);

            Assert.True(ImageStore.SaveAs(source, target));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveAsOverwritesAnExistingTarget()
    {
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, HexA + ".png");
            var target = Path.Combine(dir, "saved.png");
            File.WriteAllBytes(source, [9]);
            File.WriteAllBytes(target, [1, 1, 1]);

            Assert.True(ImageStore.SaveAs(source, target));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveAsReturnsFalseForAMissingSource()
    {
        var dir = NewDir();
        try
        {
            Assert.False(ImageStore.SaveAs(Path.Combine(dir, "gone.png"), Path.Combine(dir, "out.png")));
            Assert.False(File.Exists(Path.Combine(dir, "out.png")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MoveToTrashAndRestoreRoundTrip()
    {
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, HexA + ".png");
            File.WriteAllBytes(source, [1, 2, 3]);

            var trash = ImageStore.MoveToTrash(source);

            Assert.NotNull(trash);
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(trash));
            Assert.True(ImageStore.RestoreFromTrash(trash!, dir));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(trash));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepTrashRemovesOnlyExpiredEntries()
    {
        var dir = NewDir();
        try
        {
            var trashDir = Path.Combine(dir, "trash");
            Directory.CreateDirectory(trashDir);
            var oldFile = Path.Combine(trashDir, HexA + ".png");
            var freshFile = Path.Combine(trashDir, HexB + ".png");
            File.WriteAllBytes(oldFile, [1]);
            File.WriteAllBytes(freshFile, [2]);
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-2));

            var removed = ImageStore.SweepTrash(dir, TimeSpan.FromHours(1));

            Assert.Equal(1, removed);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MoveToTrashReturnsNullWithoutAFile()
    {
        var dir = NewDir();
        try
        {
            Assert.Null(ImageStore.MoveToTrash(Path.Combine(dir, "missing.png")));
            Assert.Null(ImageStore.MoveToTrash(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepOrphansReturnsZeroForMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbar-missing-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, ImageStore.SweepOrphans(dir, Array.Empty<string>()));
    }

    [Fact]
    public void SweepOrphansDeletesUnreferencedHashedPngs()
    {
        var dir = NewDir();
        try
        {
            var kept = Path.Combine(dir, HexA + ".png");
            var orphan = Path.Combine(dir, HexB + ".png");
            File.WriteAllText(kept, "a");
            File.WriteAllText(orphan, "b");

            var removed = ImageStore.SweepOrphans(dir, new[] { kept });

            Assert.Equal(1, removed);
            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(orphan));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepOrphansLeavesFilesThisStoreWouldNotCreate()
    {
        var dir = NewDir();
        try
        {
            // A user's own file, a non-hex name and a hex name of the wrong length must all survive.
            File.WriteAllText(Path.Combine(dir, "notes.png"), "x");
            File.WriteAllText(Path.Combine(dir, "0123456789abcdef.png"), "x");
            File.WriteAllText(Path.Combine(dir, HexA + "-copy.png"), "x");
            File.WriteAllText(Path.Combine(dir, HexA + ".jpg"), "x");

            Assert.Equal(0, ImageStore.SweepOrphans(dir, Array.Empty<string>()));
            Assert.Equal(4, Directory.GetFiles(dir).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepOrphansMatchesReferencesCaseInsensitively()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, HexA.ToUpperInvariant() + ".png");
            File.WriteAllText(path, "x");

            var removed = ImageStore.SweepOrphans(dir, new[] { Path.Combine(dir, HexA + ".png") });

            Assert.Equal(0, removed);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepOrphansIgnoresBlankReferences()
    {
        var dir = NewDir();
        try
        {
            var orphan = Path.Combine(dir, HexB + ".png");
            File.WriteAllText(orphan, "x");

            // A row with no image (a text clip) stores null/empty; that must not throw.
            var removed = ImageStore.SweepOrphans(dir, new[] { null!, "", "   " });

            Assert.Equal(1, removed);
            Assert.False(File.Exists(orphan));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteToleratesBlankAndMissingPaths()
    {
        Assert.Null(Record.Exception(() => ImageStore.TryDelete(null)));
        Assert.Null(Record.Exception(() => ImageStore.TryDelete("  ")));
        Assert.Null(Record.Exception(
            () => ImageStore.TryDelete(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")[..8] + ".png"))));
    }
}
