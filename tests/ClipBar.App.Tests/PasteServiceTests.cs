using System.IO;
using ClipBar.App.Services;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.1 hardening (found while reviewing the paste path): <c>SetClipboardAsync</c> used to
/// return void, so <c>PasteAsync</c> sent Ctrl+V even when nothing had been written — an image
/// whose file had been trimmed away, an empty file list, or a clipboard another process held for
/// good. The keystroke then injected whatever the clipboard held *before*, i.e. the previous clip.
/// <c>DescribeUncopyable</c> is the pure gate that makes that rule testable without a live clipboard.
/// </summary>
public class PasteServiceTests
{
    private static ClipItem Text(string? text = "hello") => new()
    {
        Kind = ClipKind.Text,
        Text = text,
    };

    private static ClipItem Image(string? path) => new()
    {
        Kind = ClipKind.Image,
        ImagePath = path,
    };

    private static ClipItem Files(params string[] paths) => new()
    {
        Kind = ClipKind.Files,
        FilePaths = [.. paths],
    };

    private static readonly Func<string, bool> NothingExists = _ => false;
    private static readonly Func<string, bool> EverythingExists = _ => true;

    [Fact]
    public void TextIsAlwaysCopyable()
    {
        Assert.Null(PasteService.DescribeUncopyable(Text(), NothingExists));
        Assert.Null(PasteService.DescribeUncopyable(Text(string.Empty), NothingExists));
        Assert.Null(PasteService.DescribeUncopyable(Text(null), NothingExists));
    }

    [Fact]
    public void ImageWithMissingFileIsNotCopyable()
    {
        // The orphan sweep / trim deleted the PNG after the row was captured.
        Assert.Equal("image file is gone", PasteService.DescribeUncopyable(Image(@"C:\gone\x.png"), NothingExists));
    }

    [Fact]
    public void ImageWithPresentFileIsCopyable()
    {
        Assert.Null(PasteService.DescribeUncopyable(Image(@"C:\there\x.png"), EverythingExists));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ImageWithoutPathIsNotCopyable(string? path)
    {
        Assert.Equal("image clip has no path", PasteService.DescribeUncopyable(Image(path), EverythingExists));
    }

    [Fact]
    public void EmptyFileListIsNotCopyable()
    {
        Assert.Equal("file list is empty", PasteService.DescribeUncopyable(Files(), EverythingExists));
    }

    [Fact]
    public void NonEmptyFileListIsCopyable()
    {
        Assert.Null(PasteService.DescribeUncopyable(Files(@"C:\a.txt"), NothingExists));
        Assert.Null(PasteService.DescribeUncopyable(Files(@"C:\a.txt", @"C:\b.txt"), NothingExists));
    }

    [Fact]
    public void UnknownKindIsNotCopyable()
    {
        // A row written by a future version with a kind this build does not know: copying it
        // would put nothing on the clipboard while still reporting success.
        var item = new ClipItem { Kind = (ClipKind)99 };
        Assert.StartsWith("unknown clip kind", PasteService.DescribeUncopyable(item, EverythingExists));
    }

    /// <summary>
    /// End to end on the real service: an uncopyable clip must come back <c>CopyFailed</c>, and the
    /// suppression must already have been armed (so the monitor does not record our own retry as a
    /// new clip). Deliberately asserts nothing about clipboard *contents*: the clipboard is a
    /// process-wide singleton, and reading or writing it here would both race with any running
    /// instance and push sentinel text into the user's real history. The "nothing is written"
    /// property itself is pinned by <see cref="DescribeUncopyable"/> above, which runs before any
    /// clipboard call.
    /// </summary>
    [Theory]
    [InlineData(ClipKind.Image)]
    [InlineData(ClipKind.Files)]
    public async Task UncopyableClipReturnsCopyFailed(ClipKind kind)
    {
        var item = kind == ClipKind.Image ? Image(@"C:\does\not\exist\x.png") : Files();
        var suppressed = 0;
        var service = new PasteService((_, _) => Interlocked.Increment(ref suppressed));

        var outcome = await service.PasteAsync(item, IntPtr.Zero, paste: true);

        Assert.Equal(PasteOutcome.CopyFailed, outcome);
        Assert.Equal(1, Volatile.Read(ref suppressed));
    }

    /// <summary>
    /// F-42, measured on the real decode path: a damaged image file throws
    /// <c>NotSupportedException</c> / <c>FileFormatException</c> out of <c>EndInit</c>, neither of
    /// which is the <c>COMException</c> the clipboard retry loop catches. <c>File.Exists</c> says
    /// true for every one of these shapes, so the uncopyable gate let them through and the
    /// exception reached <c>PasteItem</c> (async void) with the panel already hidden — no paste,
    /// no message, just a log line. The decode is now guarded, so the outcome is a plain
    /// <c>CopyFailed</c> the caller already knows how to report.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("garbage")]
    public async Task DamagedImageFileReturnsCopyFailedInsteadOfThrowing(string shape)
    {
        var path = Path.Combine(NewDir(), shape + ".png");
        WriteDamagedPng(path, shape);

        var service = new PasteService((_, _) => { });
        var outcome = await service.PasteAsync(Image(path), IntPtr.Zero, paste: true);

        Assert.Equal(PasteOutcome.CopyFailed, outcome);
    }

    [Fact]
    public async Task MalformedImagePathReturnsCopyFailedInsteadOfThrowing()
    {
        // UriFormatException, the third shape the clipboard loop never caught.
        var service = new PasteService((_, _) => { });
        var outcome = await service.PasteAsync(Image("::::"), IntPtr.Zero, paste: true);

        Assert.Equal(PasteOutcome.CopyFailed, outcome);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbar-paste-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The three damaged shapes the probe measured, written byte-exactly.</summary>
    private static void WriteDamagedPng(string path, string shape)
    {
        switch (shape)
        {
            case "empty":
                // Crash or a full disk between CreateNew and the first write.
                File.WriteAllBytes(path, Array.Empty<byte>());
                break;
            case "truncated":
                // A valid PNG signature followed by nothing decodable.
                var bytes = new byte[512];
                byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                Array.Copy(magic, bytes, magic.Length);
                File.WriteAllBytes(path, bytes);
                break;
            default:
                // Plain text with a .png name.
                File.WriteAllText(path, "this is not a png at all, it is plain text");
                break;
        }
    }

    [Fact]
    public async Task UnknownKindReturnsCopyFailed()
    {
        var service = new PasteService((_, _) => { });
        var outcome = await service.PasteAsync(new ClipItem { Kind = (ClipKind)99 }, IntPtr.Zero, paste: true);

        Assert.Equal(PasteOutcome.CopyFailed, outcome);
    }
}
