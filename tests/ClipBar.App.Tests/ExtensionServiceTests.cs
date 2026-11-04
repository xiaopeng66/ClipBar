using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClipBar.App.Services;
using ClipBar.Core.Extensions;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.1 T-01..T-06: hostile .pbar packages driven through the real importer, against a
/// throwaway extensions store. The store path is injected, so nothing here reads or writes
/// %LOCALAPPDATA%\ClipBar.
/// </summary>
public class ExtensionServiceTests
{
    private const string SimpleAction = """{ "title": "Run", "url": "https://example.com/" }""";

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "clipbar-ext-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            Extensions = Path.Combine(Root, "extensions");
        }

        public string Root { get; }

        public string Extensions { get; }

        public ExtensionService NewService() => new(Extensions);

        public string NewPackagePath()
        {
            var directory = Path.Combine(Root, "packages", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "package.pbar");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp tree is not worth failing an assertion over.
            }
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static string Manifest(string identifier, string action = SimpleAction) => $$"""
        {
          "name": "Hostile",
          "identifier": {{JsonSerializer.Serialize(identifier)}},
          "actions": [ {{action}} ]
        }
        """;

    private static string WritePackage(Workspace workspace, string manifestJson, Action<ZipArchive>? extra = null)
    {
        var path = workspace.NewPackagePath();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "extension.json", manifestJson);
            extra?.Invoke(archive);
        }

        return path;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content = "",
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteZeroEntry(ZipArchive archive, string name, long bytes, CompressionLevel level)
    {
        // Streamed in 1 MiB chunks, so building the bomb never materialises `bytes` of memory.
        var entry = archive.CreateEntry(name, level);
        using var target = entry.Open();
        var chunk = new byte[1024 * 1024];
        while (bytes > 0)
        {
            var count = (int)Math.Min(chunk.Length, bytes);
            target.Write(chunk, 0, count);
            bytes -= count;
        }
    }

    private static void AssertStoreSurvived(Workspace workspace)
    {
        // The extension store is a sibling of the data folder, never a casualty of a bad
        // identifier — the F-34 regression was exactly DeleteInside wiping it.
        Assert.True(Directory.Exists(workspace.Root), "工作目录被导入删掉了。");
        Assert.True(Directory.Exists(workspace.Extensions), "extensions 目录被导入删掉了。");
    }

    private static void AssertNoStagingLeftovers(Workspace workspace)
    {
        if (!Directory.Exists(workspace.Extensions))
        {
            return;
        }

        Assert.Empty(Directory.GetDirectories(workspace.Extensions, ".import-*"));
    }

    private static void AssertOnlyFolderIsInsideStore(Workspace workspace, string folderPath)
    {
        var only = Assert.Single(Directory.GetDirectories(workspace.Extensions));
        Assert.Equal(only, folderPath);
        Assert.False(string.Equals(
            Path.GetFileName(folderPath),
            ".",
            StringComparison.Ordinal));
        Assert.False(string.Equals(
            Path.GetFileName(folderPath),
            "..",
            StringComparison.Ordinal));
    }

    // ---- T-01 / T-02 / T-03: hostile identifiers --------------------------

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(" . . ")]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public async Task UnusableIdentifierFallsBackToAGeneratedFolder(string identifier)
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest(identifier));

        var loaded = await workspace.NewService().ImportAsync(package);

        // The manifest keeps its authored id; only the on-disk folder is substituted.
        Assert.Equal(identifier.Trim(), loaded.Extension.Identifier);
        Assert.StartsWith("extension-", Path.GetFileName(loaded.FolderPath));
        AssertOnlyFolderIsInsideStore(workspace, loaded.FolderPath);
        AssertStoreSurvived(workspace);
        AssertNoStagingLeftovers(workspace);
    }

    [Theory]
    [InlineData("a/b", "a-b")]
    [InlineData("a\\b", "a-b")]
    [InlineData("a:b", "a-b")]
    [InlineData("tab\there", "tab-here")]
    [InlineData("  padded  ", "padded")]
    public async Task SlashedIdentifierBecomesOneFolderSegment(string identifier, string expected)
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest(identifier));

        var loaded = await workspace.NewService().ImportAsync(package);

        Assert.Equal(expected, Path.GetFileName(loaded.FolderPath));
        AssertOnlyFolderIsInsideStore(workspace, loaded.FolderPath);
        AssertStoreSurvived(workspace);
    }

    [Fact]
    public async Task OverlongIdentifierIsTruncatedToTheSegmentCap()
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest(new string('a', 200)));

        var loaded = await workspace.NewService().ImportAsync(package);

        Assert.Equal(64, Path.GetFileName(loaded.FolderPath).Length);
        AssertStoreSurvived(workspace);
    }

    // ---- T-04: zip-slip --------------------------------------------------

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    public async Task ArchiveEntryOutsideThePackageIsRejected(string entryName)
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest("traversal"), archive => WriteEntry(archive, entryName, "pwned"));

        await Assert.ThrowsAsync<SnipDoManifestException>(() => workspace.NewService().ImportAsync(package));

        // Nothing may have landed outside the staging folder — not one level up, and not at
        // the drive root that "C:\evil.txt" would resolve to.
        Assert.False(File.Exists(Path.Combine(workspace.Root, "evil.txt")));
        Assert.False(File.Exists(@"C:\evil.txt"));
        Assert.Empty(Directory.GetFiles(workspace.Root, "evil.txt", SearchOption.AllDirectories));
        AssertStoreSurvived(workspace);
        AssertNoStagingLeftovers(workspace);
    }

    // ---- T-05 / T-06: archive bombs --------------------------------------

    [Fact]
    public async Task PackageWithTooManyEntriesIsRejected()
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest("many-entries"), archive =>
        {
            for (var i = 0; i < 5000; i++)
            {
                WriteEntry(archive, $"f{i}.txt");
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.NewService().ImportAsync(package));

        AssertStoreSurvived(workspace);
        AssertNoStagingLeftovers(workspace);
        Assert.Empty(Directory.GetDirectories(workspace.Extensions));
    }

    [Fact]
    public async Task OversizedEntryIsRejectedBeforeAnythingIsInflated()
    {
        // T-06's shape: one entry past the per-entry ceiling. The declared length is visible in
        // the central directory, so ArchiveLimits.Validate refuses the package up front and no
        // entry body is ever written.
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest("big-entry"), archive =>
            WriteZeroEntry(archive, "bomb.txt", 300L * 1024 * 1024, CompressionLevel.Fastest));

        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.NewService().ImportAsync(package));

        AssertStoreSurvived(workspace);
        AssertNoStagingLeftovers(workspace);
        Assert.False(File.Exists(Path.Combine(workspace.Extensions, "bomb.txt")));
    }

    [Fact]
    public async Task HighlyCompressedEntryIsCaughtByTheRatioCheckAndCleanedUp()
    {
        // 16 MiB of zeros at SmallestSize compresses past 1000:1 while staying under the
        // per-entry cap, so only the streaming ratio check can stop it. The entry body is
        // written to staging first, which also proves the failure path deletes the staging tree.
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest("ratio-bomb"), archive =>
            WriteZeroEntry(archive, "bomb.txt", 16L * 1024 * 1024, CompressionLevel.SmallestSize));

        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.NewService().ImportAsync(package));

        AssertStoreSurvived(workspace);
        AssertNoStagingLeftovers(workspace);
        Assert.Empty(Directory.GetDirectories(workspace.Extensions));
    }

    // ---- happy path, so the rejections above are not vacuous -------------

    [Fact]
    public async Task OrdinaryPackageImportsAndReloads()
    {
        using var workspace = new Workspace();
        var package = WritePackage(workspace, Manifest("community.example", """
            { "title": "Open", "url": "https://example.com/{PLAIN_TEXT}" }
            """), archive => WriteEntry(archive, "icons/a.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", CompressionLevel.Optimal));

        var loaded = await workspace.NewService().ImportAsync(package);

        Assert.Equal("community.example", loaded.Extension.Identifier);
        Assert.Equal("community.example", Path.GetFileName(loaded.FolderPath));
        Assert.Equal("Open", loaded.Extension.Actions[0].Title);
        Assert.True(File.Exists(Path.Combine(loaded.FolderPath, "icons", "a.svg")));
        AssertNoStagingLeftovers(workspace);
    }

    [Fact]
    public async Task ReimportingTheSameIdentifierReplacesTheFolder()
    {
        using var workspace = new Workspace();
        var first = WritePackage(workspace, Manifest("same.id"));
        await workspace.NewService().ImportAsync(first);

        var second = WritePackage(workspace, Manifest("same.id"), archive =>
            WriteEntry(archive, "new.txt", "second", CompressionLevel.Optimal));
        await workspace.NewService().ImportAsync(second);

        var folder = Path.Combine(workspace.Extensions, "same.id");
        Assert.True(File.Exists(Path.Combine(folder, "new.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "extension.json.corrupt")));
        Assert.Single(Directory.GetDirectories(workspace.Extensions));
    }
}
