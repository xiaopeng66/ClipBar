using System.IO;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-43: a scripted CLI call must be able to tell success from failure. The old code
/// swallowed the error and still exited 0, so a broken deployment looked healthy.
/// </summary>
public class PluginCommandRunnerTests
{
    [Fact]
    public void NoSwitchMeansNotRequested()
    {
        Assert.False(PluginCommandRunner.IsRequested([]));
        Assert.False(PluginCommandRunner.IsRequested(["--show"]));
    }

    [Fact]
    public void SwitchesAreCaseInsensitive()
    {
        Assert.True(PluginCommandRunner.IsRequested(["--export-snipdo"]));
        Assert.True(PluginCommandRunner.IsRequested(["--EXPORT-SNIPDO"]));
        Assert.True(PluginCommandRunner.IsRequested(["--install-snipdo"]));
    }

    [Fact]
    public void ASuccessfulExportExitsZero()
    {
        using var dir = new TempDir();
        var log = new List<string>();

        var code = PluginCommandRunner.Run(
            ["--export-snipdo"],
            () => new SnipDoPluginExporter(dir.Path, () => @"C:\fake\ClipBar.exe"),
            log.Add,
            (_, _) => { });

        Assert.Equal(0, code);
        Assert.Contains(log, line => line.StartsWith("SnipDo plugin exported"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "ClipBar.pbar")));
    }

    [Fact]
    public void AFailingExportExitsOne()
    {
        var errors = new List<string>();

        var code = PluginCommandRunner.Run(
            ["--export-snipdo"],
            () => throw new IOException("磁盘只读"),
            _ => { },
            (context, _) => errors.Add(context));

        Assert.Equal(1, code);
        Assert.Contains("SnipDo plugin command", errors);
    }

    [Fact]
    public void ARealWriteFailureIsReportedNotSwallowed()
    {
        using var dir = new TempDir();

        // A file where the exporter wants a directory: Export() throws from CreateDirectory,
        // which is the closest headless stand-in for "the data folder is not writable".
        var blocker = Path.Combine(dir.Path, "blocked");
        File.WriteAllText(blocker, "x");
        var errors = new List<string>();

        var code = PluginCommandRunner.Run(
            ["--export-snipdo"],
            () => new SnipDoPluginExporter(blocker, () => @"C:\fake\ClipBar.exe"),
            _ => { },
            (context, _) => errors.Add(context));

        Assert.Equal(1, code);
        Assert.Contains("SnipDo plugin command", errors);
    }

    /// <summary>A scratch directory beside the test binaries, removed on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "test-tmp",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
