using System.IO;
using System.IO.Compression;

namespace ClipBar.Core.Security;

/// <summary>
/// Resource limits for reading third-party archives. A .pbar is a zip the user downloaded,
/// so it may be a zip bomb: the only defence is to bound the number of entries and the total
/// number of uncompressed bytes while extracting, never after.
/// </summary>
public static class ArchiveLimits
{
    /// <summary>Maximum number of entries we will unpack.</summary>
    public const int MaxEntries = 2048;

    /// <summary>Maximum uncompressed size of a single entry.</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>Maximum total uncompressed size of the whole package.</summary>
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Compressed payloads above this size skip the ratio check: a legitimately large, mostly
    /// incompressible asset (an embedded PNG) can reach a high ratio without being a bomb.
    /// </summary>
    private const long RatioCheckFloorBytes = 8L * 1024 * 1024;

    /// <summary>Reject packages whose compression ratio exceeds this once past the floor.</summary>
    private const double MaxCompressionRatio = 1000d;

    /// <summary>
    /// Validates the archive's entry table before anything is written to disk.
    /// Throws <see cref="InvalidDataException"/> when the package exceeds the limits.
    /// </summary>
    public static void Validate(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException(
                $"压缩包条目过多（{archive.Entries.Count}，上限 {MaxEntries}）。");
        }

        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            // Length is the uncompressed size recorded in the central directory; a hostile
            // archive can lie, so the extraction loop re-checks the bytes it actually reads.
            if (entry.Length > MaxEntryBytes)
            {
                throw new InvalidDataException(
                    $"压缩包内单个文件过大（{entry.FullName}，{entry.Length} 字节）。");
            }

            declaredTotal += entry.Length;
            if (declaredTotal > MaxTotalBytes)
            {
                throw new InvalidDataException(
                    $"压缩包解压后总体积过大（超过 {MaxTotalBytes / (1024 * 1024)} MB）。");
            }
        }
    }

    /// <summary>
    /// Tracks the bytes written so far and rejects an entry whose actual size (or compression
    /// ratio) blows past the limits while it is being extracted.
    /// </summary>
    public sealed class Budget
    {
        private long _total;

        public long Total => _total;

        public void Charge(ZipArchiveEntry entry, long bytes)
        {
            _total += bytes;
            if (_total > MaxTotalBytes)
            {
                throw new InvalidDataException(
                    $"压缩包解压后总体积过大（超过 {MaxTotalBytes / (1024 * 1024)} MB）。");
            }

            if (bytes > MaxEntryBytes)
            {
                throw new InvalidDataException($"压缩包内单个文件过大（{entry.FullName}）。");
            }

            if (bytes > RatioCheckFloorBytes && entry.CompressedLength > 0)
            {
                var ratio = (double)bytes / entry.CompressedLength;
                if (ratio > MaxCompressionRatio)
                {
                    throw new InvalidDataException(
                        $"压缩包压缩比异常（{entry.FullName}，{ratio:F0}:1），疑似压缩炸弹。");
                }
            }
        }
    }
}
