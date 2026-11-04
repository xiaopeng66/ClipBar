using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.App.Converters;

namespace ClipBar.App.Tests;

/// <summary>
/// The thumbnail converter has to keep a wide screenshot inside the card's content box. Capping
/// against the list width instead used to let the image poke out under the card padding, where it
/// was clipped — the "right edge of my screenshot is missing" report.
/// </summary>
public class ThumbnailSizeConverterTests
{
    private const double Scale = 1.25;          // 125% DPI
    private const double ContentWidth = 378.4;  // card content box at that scale
    private const double ListHeight = 598;

    private static BitmapSource Bitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static double Convert(
        BitmapSource source,
        string parameter,
        double contentWidth = ContentWidth,
        double listHeight = ListHeight,
        string mode = "Large")
        => (double)new ThumbnailSizeConverter().Convert(
            [source, Scale, contentWidth, listHeight, mode],
            typeof(double),
            parameter,
            CultureInfo.InvariantCulture);

    [Fact]
    public void WideImageIsCappedToTheContentWidthAndKeepsItsAspect()
    {
        var source = Bitmap(1533, 428);

        var width = Convert(source, "Width");
        var height = Convert(source, "Height");

        Assert.Equal(ContentWidth, width, 3);
        // Floating point rounding may land an ulp above the cap; anything more would overflow.
        Assert.True(width <= ContentWidth + 0.01);
        // 1533 x 428 is 3.583:1; the capped box has to stay that shape.
        Assert.Equal(1533.0 / 428.0, width / height, 3);
    }

    [Fact]
    public void TallImageIsCappedToTheHeightFractionAndKeepsItsAspect()
    {
        var source = Bitmap(900, 1600);

        var width = Convert(source, "Width");
        var height = Convert(source, "Height");

        Assert.Equal(ListHeight * 0.75, height, 3);
        Assert.True(width <= ContentWidth);
        Assert.Equal(900.0 / 1600.0, width / height, 3);
    }

    [Fact]
    public void NarrowImageKeepsItsNaturalPixelSize()
    {
        var source = Bitmap(120, 80);

        // Natural size is pixels / DPI scale, so the image is not stretched up.
        Assert.Equal(120 / Scale, Convert(source, "Width"), 3);
        Assert.Equal(80 / Scale, Convert(source, "Height"), 3);
    }

    [Fact]
    public void MediumModeCapsAWideImageToTheCompactBox()
    {
        var source = Bitmap(1533, 428);

        var width = Convert(source, "Width", mode: "Medium");
        var height = Convert(source, "Height", mode: "Medium");

        // 160 DIP wide is the binding constraint here; the height follows the aspect ratio.
        Assert.Equal(160, width, 3);
        Assert.Equal(1533.0 / 428.0, width / height, 3);
        Assert.True(width < Convert(source, "Width"));
    }

    [Fact]
    public void MediumModeDoesNotUpscaleASmallImage()
    {
        var source = Bitmap(60, 40);

        Assert.Equal(60 / Scale, Convert(source, "Width", mode: "Medium"), 3);
        Assert.Equal(40 / Scale, Convert(source, "Height", mode: "Medium"), 3);
    }

    [Fact]
    public void MediumModeStillRespectsANarrowContentBox()
    {
        var source = Bitmap(1533, 428);

        // A very narrow panel must not let the fixed 160 DIP box overflow the card.
        Assert.Equal(100, Convert(source, "Width", contentWidth: 100, mode: "Medium"), 3);
    }

    [Fact]
    public void SmallModeFitsTheLongestSideIntoThe40DipIconAndKeepsAspect()
    {
        var source = Bitmap(200, 100);

        var width = Convert(source, "Width", mode: "Small");
        var height = Convert(source, "Height", mode: "Small");

        Assert.Equal(40, width, 3);
        Assert.Equal(20, height, 3);
        Assert.Equal(2, width / height, 3);
    }

    [Fact]
    public void SmallModeUsesThe40DipHeightForAPortraitImage()
    {
        var source = Bitmap(100, 200);

        var width = Convert(source, "Width", mode: "Small");
        var height = Convert(source, "Height", mode: "Small");

        Assert.Equal(20, width, 3);
        Assert.Equal(40, height, 3);
        Assert.Equal(0.5, width / height, 3);
    }

    [Fact]
    public void SmallModeDoesNotUpscaleASmallImage()
    {
        var source = Bitmap(32, 24);

        Assert.Equal(32 / Scale, Convert(source, "Width", mode: "Small"), 3);
        Assert.Equal(24 / Scale, Convert(source, "Height", mode: "Small"), 3);
    }

    [Fact]
    public void ZeroContentWidthFallsBackToTheNaturalSize()
    {
        var source = Bitmap(1533, 428);

        // Before the first layout pass the bound ActualWidth is 0; the converter must not divide
        // by it nor return something negative.
        var width = Convert(source, "Width", contentWidth: 0);

        Assert.Equal(1533 / Scale, width, 3);
    }
}
