using System.IO;
using System.IO.Compression;
using ClipBar.Core.Security;

namespace ClipBar.Core.Tests;

public class ArchiveLimitsTests
{
    private static ZipArchive CreateArchive(params (string Name, long ZeroBytes)[] entries) =>
        CreateArchive(CompressionLevel.Fastest, entries);

    private static ZipArchive CreateArchive(CompressionLevel level, params (string Name, long ZeroBytes)[] entries)
    {
        // The archive is built from zero bytes, which deflate folds down hard: a 256 MiB payload
        // costs well under 2 MiB of memory here, so the size limits can be tested for real
        // instead of with a mocked entry table.
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, zeroBytes) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var stream = entry.Open();
                WriteZeros(stream, zeroBytes);
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static void WriteZeros(Stream stream, long bytes)
    {
        var chunk = new byte[1024 * 1024];
        while (bytes > 0)
        {
            var count = (int)Math.Min(chunk.Length, bytes);
            stream.Write(chunk, 0, count);
            bytes -= count;
        }
    }

    [Fact]
    public void ValidateAcceptsOrdinaryPackage()
    {
        using var archive = CreateArchive(("data.json", 1024), ("icons/a.svg", 512));
        ArchiveLimits.Validate(archive);
    }

    [Fact]
    public void ValidateRejectsTooManyEntries()
    {
        var entries = new (string, long)[ArchiveLimits.MaxEntries + 1];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = ($"f{i}.txt", 0);
        }

        using var archive = CreateArchive(entries);
        Assert.Throws<InvalidDataException>(() => ArchiveLimits.Validate(archive));
    }

    [Fact]
    public void ValidateRejectsOversizedSingleEntry()
    {
        using var archive = CreateArchive(("bomb.bin", ArchiveLimits.MaxEntryBytes + 1024));
        Assert.Throws<InvalidDataException>(() => ArchiveLimits.Validate(archive));
    }

    [Fact]
    public void ValidateRejectsOversizedTotal()
    {
        // 33 × 8 MiB = 264 MiB, past the 256 MiB ceiling, while each entry stays under the
        // per-entry cap — so only the running total can trip the check.
        const long eightMiB = 8L * 1024 * 1024;
        var entries = new (string, long)[33];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = ($"part{i}.bin", eightMiB);
        }

        using var archive = CreateArchive(entries);
        Assert.Throws<InvalidDataException>(() => ArchiveLimits.Validate(archive));
    }

    [Fact]
    public void BudgetRejectsEntryPastPerFileLimit()
    {
        using var archive = CreateArchive(("big.bin", 64));
        var budget = new ArchiveLimits.Budget();

        Assert.Throws<InvalidDataException>(
            () => budget.Charge(archive.Entries[0], ArchiveLimits.MaxEntryBytes + 1));
    }

    [Fact]
    public void BudgetRejectsRunningTotalPastLimit()
    {
        // Genuine entry, so the charged bytes match what was actually stored and neither the
        // per-entry cap nor the ratio check can fire first. Four 64 MiB charges land exactly on
        // the 256 MiB ceiling, which is still allowed; the fifth is what crosses it.
        using var archive = CreateArchive(("big.bin", ArchiveLimits.MaxEntryBytes));
        var entry = archive.Entries[0];
        Assert.Equal(ArchiveLimits.MaxEntryBytes, entry.Length);

        var budget = new ArchiveLimits.Budget();
        for (var i = 0; i < 4; i++)
        {
            budget.Charge(entry, entry.Length);
        }

        Assert.Equal(ArchiveLimits.MaxTotalBytes, budget.Total);
        Assert.Throws<InvalidDataException>(() => budget.Charge(entry, entry.Length));
    }

    [Fact]
    public void BudgetRejectsCompressionBombRatio()
    {
        // 16 MiB of zeros at SmallestSize compresses to ~16 KiB: past the 8 MiB ratio floor and
        // just over 1000:1. Fastest only reaches ~230:1, so the level matters here.
        using var archive = CreateArchive(CompressionLevel.SmallestSize, ("bomb.txt", 16L * 1024 * 1024));
        var entry = archive.Entries[0];
        Assert.True(entry.CompressedLength > 0);
        Assert.True((double)entry.Length / entry.CompressedLength > 1000);

        var budget = new ArchiveLimits.Budget();
        Assert.Throws<InvalidDataException>(() => budget.Charge(entry, entry.Length));
    }

    [Fact]
    public void BudgetSkipsRatioCheckBelowFloor()
    {
        // Under the floor the ratio is not consulted, so a tiny highly-compressed entry passes.
        using var archive = CreateArchive(CompressionLevel.SmallestSize, ("small.txt", 1024));
        var entry = archive.Entries[0];

        var budget = new ArchiveLimits.Budget();
        budget.Charge(entry, entry.Length);
        Assert.Equal(entry.Length, budget.Total);
    }
}
