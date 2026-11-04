using System.IO;
using ClipBar.Core.Security;

namespace ClipBar.Core.Tests;

public class SafePathTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipbar-safe-path-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData("com.example.thing", "com.example.thing")]
    [InlineData("  padded  ", "padded")]
    [InlineData("..x", "x")]
    [InlineData("...name...", "name")]
    [InlineData("a/b", "a-b")]
    [InlineData("a:b", "a-b")]
    [InlineData("tab\there", "tab-here")]
    public void SlugKeepsSafeIdentifiers(string identifier, string expected)
    {
        Assert.Equal(expected, SafePath.Slug(identifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(" . . ")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void SlugRejectsUnusableIdentifiers(string? identifier)
    {
        Assert.Null(SafePath.Slug(identifier));
    }

    [Fact]
    public void SlugTruncatesToMaxSegmentLength()
    {
        var slug = SafePath.Slug(new string('a', 200));
        Assert.Equal(SafePath.MaxSegmentLength, slug!.Length);
    }

    [Fact]
    public void SlugRejectsOverlongIdentifierThatTrimsToNothing()
    {
        // 70 dots would trim away entirely, leaving no usable segment.
        Assert.Null(SafePath.Slug(new string('.', 70)));
    }

    [Fact]
    public void SlugOrFallbackGeneratesUniqueNames()
    {
        var first = SafePath.SlugOrFallback("..");
        var second = SafePath.SlugOrFallback(null);

        Assert.StartsWith("extension-", first);
        Assert.StartsWith("extension-", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EnsureInsideAcceptsRelativeChild()
    {
        var root = NewRoot();
        try
        {
            var resolved = SafePath.EnsureInside(root, "icons/a.svg");
            Assert.Equal(Path.Combine(root, "icons", "a.svg"), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    public void EnsureInsideRejectsTraversal(string candidate)
    {
        var root = NewRoot();
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => SafePath.EnsureInside(root, candidate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData("/etc/passwd")]
    public void EnsureInsideRejectsRootedPaths(string candidate)
    {
        var root = NewRoot();
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => SafePath.EnsureInside(root, candidate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveInsideReturnsNullInsteadOfThrowing()
    {
        var root = NewRoot();
        try
        {
            Assert.Null(SafePath.TryResolveInside(root, null));
            Assert.Null(SafePath.TryResolveInside(root, "  "));
            Assert.Null(SafePath.TryResolveInside(root, "../escape"));
            Assert.Null(SafePath.TryResolveInside(root, @"C:\Windows"));

            var ok = SafePath.TryResolveInside(root, "data.json");
            Assert.NotNull(ok);
            Assert.True(SafePath.IsInside(root, ok!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsInsideIsStrictlyChildOnly()
    {
        var root = NewRoot();
        try
        {
            // The root, with or without a trailing separator, is not its own child — this is the
            // case that used to make DeleteInside wipe the whole extension store.
            Assert.False(SafePath.IsInside(root, root));
            Assert.False(SafePath.IsInside(root, root + Path.DirectorySeparatorChar));
            Assert.True(SafePath.IsInside(root, Path.Combine(root, "child")));

            var sibling = root + "-sibling";
            Assert.False(SafePath.IsInside(root, sibling));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsInsideTreatsPrefixSiblingsAsOutside()
    {
        // "C:\store-evil" shares a string prefix with "C:\store" but is a different directory;
        // a naive StartsWith without the separator would call it a child.
        var root = NewRoot();
        try
        {
            var evil = root + "evil";
            Directory.CreateDirectory(evil);
            try
            {
                Assert.False(SafePath.IsInside(root, evil));
            }
            finally
            {
                Directory.Delete(evil, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteInsideRefusesRootAndMissingTargets()
    {
        var root = NewRoot();
        try
        {
            Assert.False(SafePath.DeleteInside(root, root));
            Assert.False(SafePath.DeleteInside(root, Path.Combine(root, "missing")));
            Assert.True(Directory.Exists(root));

            var child = Path.Combine(root, "child");
            Directory.CreateDirectory(Path.Combine(child, "nested"));
            Assert.True(SafePath.DeleteInside(root, child));
            Assert.False(Directory.Exists(child));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
