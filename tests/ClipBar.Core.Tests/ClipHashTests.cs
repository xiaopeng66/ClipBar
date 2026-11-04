using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

public sealed class ClipHashTests
{
    [Fact]
    public void SameText_ProducesSameHash()
    {
        var a = new ClipItem { Kind = ClipKind.Text, Text = "hello world" };
        var b = new ClipItem { Kind = ClipKind.Text, Text = "hello world" };

        Assert.Equal(ClipHash.Compute(a), ClipHash.Compute(b));
    }

    [Fact]
    public void DifferentText_ProducesDifferentHash()
    {
        var a = new ClipItem { Kind = ClipKind.Text, Text = "hello" };
        var b = new ClipItem { Kind = ClipKind.Text, Text = "world" };

        Assert.NotEqual(ClipHash.Compute(a), ClipHash.Compute(b));
    }

    [Fact]
    public void FileOrder_DoesNotChangeHash()
    {
        var a = new ClipItem
        {
            Kind = ClipKind.Files,
            FilePaths = [@"C:\a.txt", @"C:\b.txt"],
        };
        var b = new ClipItem
        {
            Kind = ClipKind.Files,
            FilePaths = [@"C:\b.txt", @"C:\a.txt"],
        };

        Assert.Equal(ClipHash.Compute(a), ClipHash.Compute(b));
    }

    [Fact]
    public void ImageHash_IsStable()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(ClipHash.ComputeImage(bytes), ClipHash.ComputeImage(bytes));
    }
}
