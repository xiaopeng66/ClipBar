using System.IO;
using ClipBar.App.ViewModels;

namespace ClipBar.App.Tests;

/// <summary>
/// The row layout is decided in the view model: a non-image clip shows the text row, an image
/// clip shows either the preview (Large/Medium) or the picture inside the icon slot (Small).
/// Preview modes must not leave a duplicate information row under the image or hide the compact
/// row, including when a setting changes while the panel is already open.
/// </summary>
public class ClipItemLayoutTests
{
    [Theory]
    [InlineData("Large")]
    [InlineData("Medium")]
    public void ImageInLargeOrMediumShowsOnlyThePreview(string mode)
    {
        var layout = ClipItemViewModel.ComputeLayout(hasThumbnail: true, mode);

        Assert.True(layout.Preview);
        Assert.False(layout.TextRow);
        Assert.False(layout.IconImage);
        Assert.False(layout.IconSymbol);
    }

    [Fact]
    public void ImageInSmallModeFillsTheIconSlotAndKeepsTheTextRow()
    {
        var layout = ClipItemViewModel.ComputeLayout(hasThumbnail: true, "Small");

        Assert.False(layout.Preview);
        Assert.True(layout.TextRow);
        Assert.True(layout.IconImage);
        Assert.False(layout.IconSymbol);
    }

    [Theory]
    [InlineData("Large")]
    [InlineData("Medium")]
    [InlineData("Small")]
    public void NonImageClipAlwaysUsesTheSymbolRow(string mode)
    {
        var layout = ClipItemViewModel.ComputeLayout(hasThumbnail: false, mode);

        Assert.False(layout.Preview);
        Assert.True(layout.TextRow);
        Assert.False(layout.IconImage);
        Assert.True(layout.IconSymbol);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("thumbnail")]
    public void UnknownModeBehavesLikeLarge(string? mode)
    {
        // AppSettings.Normalize maps Original to Large, Thumbnail to Medium, and unknown values to
        // Large; the row must not end up in the compact layout if a raw value ever slips through.
        var layout = ClipItemViewModel.ComputeLayout(hasThumbnail: true, mode);

        Assert.True(layout.Preview);
        Assert.False(layout.TextRow);
    }

    [Fact]
    public void SmallIsCaseInsensitive()
    {
        var layout = ClipItemViewModel.ComputeLayout(hasThumbnail: true, "small");

        Assert.False(layout.Preview);
        Assert.True(layout.IconImage);
    }

    [Fact]
    public void ImageItemModeSwitchRaisesOnlyTheChangedLayoutNotifications()
    {
        using var image = new ExistingImageFile();
        var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
        {
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 320,
            ImageHeight = 200,
        });
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.SetImagePreviewMode("Small");

        Assert.Equal(
            [
                nameof(ClipItemViewModel.ShowPreview),
                nameof(ClipItemViewModel.ShowTextRow),
                nameof(ClipItemViewModel.ShowIconImage),
            ],
            changed);
        Assert.False(item.ShowPreview);
        Assert.True(item.ShowTextRow);
        Assert.True(item.ShowIconImage);
        Assert.False(item.ShowIconSymbol);

        changed.Clear();
        item.SetImagePreviewMode("small");
        Assert.Empty(changed);

        item.SetImagePreviewMode("Medium");

        Assert.Equal(
            [
                nameof(ClipItemViewModel.ShowPreview),
                nameof(ClipItemViewModel.ShowTextRow),
                nameof(ClipItemViewModel.ShowIconImage),
            ],
            changed);
        Assert.True(item.ShowPreview);
        Assert.False(item.ShowTextRow);
        Assert.False(item.ShowIconImage);
    }

    [Fact]
    public void UpdateImagePathTogglesThumbnailAndRowLayoutAndNotifiesThumbnail()
    {
        using var image = new ExistingImageFile();
        const string id = "image-update";
        var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
        {
            Id = id,
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImageWidth = 640,
            ImageHeight = 480,
        });
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.Update(new ClipBar.Core.Models.ClipItem
        {
            Id = id,
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 640,
            ImageHeight = 480,
        });

        Assert.True(item.HasThumbnail);
        Assert.True(item.ShowPreview);
        Assert.False(item.ShowTextRow);
        Assert.Equal(
            [
                nameof(ClipItemViewModel.HasThumbnail),
                nameof(ClipItemViewModel.ShowPreview),
                nameof(ClipItemViewModel.ShowTextRow),
                nameof(ClipItemViewModel.ShowIconSymbol),
                nameof(ClipItemViewModel.Thumbnail),
            ],
            changed);

        changed.Clear();
        item.Update(new ClipBar.Core.Models.ClipItem
        {
            Id = id,
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImageWidth = 640,
            ImageHeight = 480,
        });

        Assert.False(item.HasThumbnail);
        Assert.False(item.ShowPreview);
        Assert.True(item.ShowTextRow);
        Assert.Equal(
            [
                nameof(ClipItemViewModel.HasThumbnail),
                nameof(ClipItemViewModel.ShowPreview),
                nameof(ClipItemViewModel.ShowTextRow),
                nameof(ClipItemViewModel.ShowIconSymbol),
                nameof(ClipItemViewModel.Thumbnail),
            ],
            changed);
    }

    [Fact]
    public void ACorruptImageFallsBackToTheSymbolRowInsteadOfAnEmptyPreview()
    {
        // The file exists but cannot be decoded (0 bytes). The failure used to be cached
        // forever: an empty preview strip rendered and never recovered (G-04).
        using var image = new ExistingImageFile();
        var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
        {
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 320,
            ImageHeight = 200,
        });

        Assert.True(item.HasThumbnail);
        Assert.True(item.ShowPreview);

        Assert.Null(item.Thumbnail);          // decode fails
        Assert.False(item.HasThumbnail);      // layout falls back
        Assert.False(item.ShowPreview);
        Assert.True(item.ShowTextRow);
        Assert.False(item.ShowIconImage);
        Assert.True(item.ShowIconSymbol);
    }

    [Fact]
    public void AModelReplayDoesNotResurrectARowWhoseImageCannotBeDecoded()
    {
        // Every panel show replays the model (refresh diff → Update → ApplyModel) and
        // File.Exists stays true for a corrupt file: the decode-failure memory must survive
        // that replay, or the row flips back to a blank preview (probe-measured, G-04).
        using var image = new ExistingImageFile();
        const string id = "corrupt-replay";
        var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
        {
            Id = id,
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 320,
            ImageHeight = 200,
        });
        _ = item.Thumbnail;
        Assert.False(item.HasThumbnail);

        item.Update(new ClipBar.Core.Models.ClipItem
        {
            Id = id,
            Kind = ClipBar.Core.Models.ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 320,
            ImageHeight = 200,
        });

        Assert.False(item.HasThumbnail);
        Assert.True(item.ShowTextRow);
    }

    [Fact]
    public void UpdateSamePathAtADifferentHeightReDecodesTheThumbnail()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "clipbar-height-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "image.png");
        try
        {
            WritePng(path, 32, 24);
            const string id = "image-height";
            var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
            {
                Id = id,
                Kind = ClipBar.Core.Models.ClipKind.Image,
                ImagePath = path,
                ImageWidth = 32,
                ImageHeight = 24,
            });
            Assert.Equal(24, item.Thumbnail!.PixelHeight);

            var changed = new List<string?>();
            item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            // Same path, same width, different height — meaning the file itself was replaced
            // (the clip was re-stored at another resolution). The cached bitmap was stale (G-14).
            WritePng(path, 32, 48);
            item.Update(new ClipBar.Core.Models.ClipItem
            {
                Id = id,
                Kind = ClipBar.Core.Models.ClipKind.Image,
                ImagePath = path,
                ImageWidth = 32,
                ImageHeight = 48,
            });

            Assert.Contains(nameof(ClipItemViewModel.Thumbnail), changed);
            Assert.Equal(48, item.Thumbnail!.PixelHeight);

            changed.Clear();
            item.Update(new ClipBar.Core.Models.ClipItem
            {
                Id = id,
                Kind = ClipBar.Core.Models.ClipKind.Image,
                ImagePath = path,
                ImageWidth = 32,
                ImageHeight = 48,
            });
            Assert.DoesNotContain(nameof(ClipItemViewModel.Thumbnail), changed);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A real decodable PNG; the 0-byte fixture above covers only the failure path.</summary>
    private static void WritePng(string path, int width, int height)
    {
        var stride = width * 4;
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[stride * height], stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [Fact]
    public void SubtitleIncludesTheYearForCrossYearEntries()
    {
        // "12月30日" alone is ambiguous once the year turns over (D-G-04).
        var item = new ClipItemViewModel(new ClipBar.Core.Models.ClipItem
        {
            Kind = ClipBar.Core.Models.ClipKind.Text,
            Text = "old",
            UpdatedAt = DateTimeOffset.Now.AddYears(-1).ToUnixTimeMilliseconds(),
        });

        Assert.Contains("年", item.Subtitle);
    }

    private sealed class ExistingImageFile : IDisposable
    {
        private readonly string _directory;

        public ExistingImageFile()
        {
            _directory = Path.Combine(Path.GetTempPath(), "clipbar-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            FilePath = System.IO.Path.Combine(_directory, "image.png");
            File.WriteAllBytes(FilePath, []);
        }

        public string FilePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
