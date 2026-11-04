using System.IO;
using ClipBar.Core.Models;
using ClipBar.Core.Settings;

namespace ClipBar.Core.Tests;

/// <summary>
/// Plan §5.1 T-13: a damaged settings.json must not take the app down, must not silently
/// discard the user's configuration, and must be preserved for inspection.
/// </summary>
public class JsonSettingsStoreTests
{
    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Root = Path.Combine(Path.GetTempPath(), "clipbar-settings-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            SettingsPath = System.IO.Path.Combine(Root, "settings.json");
        }

        public string Root { get; }

        public string SettingsPath { get; }

        public JsonSettingsStore NewStore() => new(SettingsPath);

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
            }
        }
    }

    // ---- T-13: damaged files ---------------------------------------------

    [Theory]
    [InlineData("{\"MaxItems\": 500")]                 // truncated mid-object
    [InlineData("{\"MaxItems\": 500,}")]               // trailing comma is not allowed by the writer
    [InlineData("")]                                    // empty file, e.g. a failed write
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("not json at all")]
    public void TruncatedOrInvalidJsonFallsBackToDefaultsWithAWarning(string content)
    {
        using var temp = new TempStore();
        File.WriteAllText(temp.SettingsPath, content, System.Text.Encoding.UTF8);

        var store = temp.NewStore();
        var settings = store.Load();

        Assert.NotNull(store.LastLoadWarning);
        Assert.Contains("设置文件", store.LastLoadWarning!);
        AssertDefaultSettings(settings);
    }

    [Fact]
    public void BinaryGarbageFallsBackToDefaultsAndIsQuarantined()
    {
        using var temp = new TempStore();
        // A PNG signature followed by binary bytes: exactly what a mis-aimed write or a copy of
        // the wrong file looks like on disk.
        var garbage = new byte[4096];
        garbage[0] = 0x89;
        garbage[1] = (byte)'P';
        garbage[2] = (byte)'N';
        garbage[3] = (byte)'G';
        for (var i = 4; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(temp.SettingsPath, garbage);

        var store = temp.NewStore();
        var settings = store.Load();

        Assert.NotNull(store.LastLoadWarning);
        Assert.Contains("已损坏", store.LastLoadWarning!);
        AssertDefaultSettings(settings);

        // The bad file is preserved, never overwritten by defaults on the next save.
        var backups = Directory.GetFiles(temp.Root, "settings.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal(garbage.Length, new FileInfo(backups[0]).Length);

        // ...and the original path is now free for the next Save to take over.
        Assert.False(File.Exists(temp.SettingsPath));
    }

    [Fact]
    public void QuarantineThenSaveLeavesDefaultsOnDisk()
    {
        using var temp = new TempStore();
        File.WriteAllText(temp.SettingsPath, "{ broken", System.Text.Encoding.UTF8);

        var store = temp.NewStore();
        store.Load();
        store.Save(new AppSettings());

        Assert.Single(Directory.GetFiles(temp.Root, "settings.json.corrupt-*"));
        var reloaded = new JsonSettingsStore(temp.SettingsPath);
        var settings = reloaded.Load();
        Assert.Null(reloaded.LastLoadWarning);
        AssertDefaultSettings(settings);
    }

    [Fact]
    public void WarningIsClearedOnASuccessfulLoad()
    {
        using var temp = new TempStore();
        File.WriteAllText(temp.SettingsPath, "{ broken", System.Text.Encoding.UTF8);

        var store = temp.NewStore();
        Assert.NotNull(store.Load());
        Assert.NotNull(store.LastLoadWarning);

        store.Save(new AppSettings());
        store.Load();
        Assert.Null(store.LastLoadWarning);
    }

    private static void AssertDefaultSettings(AppSettings settings)
    {
        var defaults = new AppSettings();
        Assert.Equal(defaults.Hotkey, settings.Hotkey);
        Assert.Equal(defaults.MaxItems, settings.MaxItems);
        Assert.Equal(defaults.Theme, settings.Theme);
        Assert.Equal(defaults.ExcludedApps, settings.ExcludedApps);
    }

    // ---- ordinary round-trip --------------------------------------------

    [Fact]
    public void MissingFileReturnsDefaultsWithoutAWarning()
    {
        using var temp = new TempStore();
        var store = temp.NewStore();

        var settings = store.Load();

        Assert.Null(store.LastLoadWarning);
        AssertDefaultSettings(settings);
    }

    [Fact]
    public void SaveRoundTripsEveryField()
    {
        using var temp = new TempStore();
        var store = temp.NewStore();
        var original = new AppSettings
        {
            Hotkey = "Ctrl+Alt+V",
            MaxItems = 42,
            Theme = "Dark",
            PasteOnClick = false,
            StartWithWindows = true,
            CaptureImages = false,
            CaptureFiles = false,
            CaptureHtml = false,
            ExcludedApps = ["custom.exe"],
        };

        store.Save(original);
        var reopened = temp.NewStore();
        var loaded = reopened.Load();

        Assert.Null(reopened.LastLoadWarning);
        Assert.Equal("Ctrl+Alt+V", loaded.Hotkey);
        Assert.Equal(42, loaded.MaxItems);
        Assert.Equal("Dark", loaded.Theme);
        Assert.False(loaded.PasteOnClick);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.CaptureImages);
        Assert.False(loaded.CaptureFiles);
        Assert.False(loaded.CaptureHtml);
        Assert.Equal("custom.exe", Assert.Single(loaded.ExcludedApps));
    }

    [Fact]
    public void SaveIsCaseInsensitiveOnRead()
    {
        using var temp = new TempStore();
        File.WriteAllText(
            temp.SettingsPath,
            """{ "maxitems": 7, "HOTKEY": "Ctrl+Shift+V" }""",
            System.Text.Encoding.UTF8);

        var loaded = temp.NewStore().Load();

        Assert.Equal(7, loaded.MaxItems);
        Assert.Equal("Ctrl+Shift+V", loaded.Hotkey);
    }

    [Fact]
    public void SaveLeavesNoTempFileBehind()
    {
        using var temp = new TempStore();
        var store = temp.NewStore();

        store.Save(new AppSettings());
        store.Save(new AppSettings());

        Assert.Empty(Directory.GetFiles(temp.Root, "*.tmp"));
        Assert.True(File.Exists(temp.SettingsPath));
    }

    [Fact]
    public void OutOfRangeValuesAreClampedNotTrusted()
    {
        // Settings come from a file the user can edit. The store itself is a dumb reader; the
        // clamp lives in App startup and SaveSettings, so this pins the store's behaviour
        // (negative survives the read) and documents that the caller is responsible.
        using var temp = new TempStore();
        File.WriteAllText(
            temp.SettingsPath,
            """{ "MaxItems": -5, "Theme": "nonsense", "Hotkey": "" }""",
            System.Text.Encoding.UTF8);

        var loaded = temp.NewStore().Load();

        Assert.Equal(-5, loaded.MaxItems);
        Assert.Equal("nonsense", loaded.Theme);
        Assert.Equal(string.Empty, loaded.Hotkey);
    }

    // ---- G-03: explicit null fields (valid JSON, hostile values) ----------

    [Fact]
    public void NullFieldsLoadCleanlyAndNormalizeWithoutAWarning()
    {
        // A hand-edited file can carry explicit nulls. This used to NRE in AppSettings.Normalize
        // *after* Load returned — bypassing the quarantine and crashing every startup (G-03).
        using var temp = new TempStore();
        File.WriteAllText(
            temp.SettingsPath,
            """{ "Hotkey": null, "Theme": null, "ImagePreview": null, "ExcludedApps": null, "MaxItems": 300 }""",
            System.Text.Encoding.UTF8);

        var store = temp.NewStore();
        var loaded = store.Load().Normalize();

        Assert.Null(store.LastLoadWarning);
        Assert.Equal(300, loaded.MaxItems);
        Assert.Equal(string.Empty, loaded.Hotkey);
        Assert.Equal(string.Empty, loaded.Theme);
        Assert.Equal("Large", loaded.ImagePreview);
        Assert.Empty(loaded.ExcludedApps);
    }

    // ---- G-24: legacy preview values survive a full store round trip ------

    [Theory]
    [InlineData("Original", "Large")]
    [InlineData("Thumbnail", "Medium")]
    [InlineData("Small", "Small")]
    public void LegacyPreviewValuesSurviveSaveLoadAndNormalize(string stored, string expected)
    {
        using var temp = new TempStore();
        var store = temp.NewStore();

        store.Save(new AppSettings { ImagePreview = stored });
        Assert.Contains($"\"ImagePreview\": \"{stored}\"", File.ReadAllText(temp.SettingsPath));

        var loaded = store.Load().Normalize();
        Assert.Equal(expected, loaded.ImagePreview);
    }
}
