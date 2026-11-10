using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClipBar.App.Converters;

/// <summary>
/// Returns the preview's width or height so that one image pixel covers one device pixel
/// (a WPF bitmap's natural size is DIP at 96 DPI, so at 125%/200% scaling it would be stretched
/// and blurred), then shrinks it to fit, keeping the aspect ratio. Images are never upscaled.
/// Values: bitmap, DPI scale, card content width, list height, preview mode.
/// Parameter selects "Width" or "Height".
/// </summary>
public sealed class ThumbnailSizeConverter : IMultiValueConverter
{
    /// <summary>
    /// A full-size preview may fill the card's whole content box; the card's own 8 DIP padding is
    /// what separates the image from the rounded edge. Capping against the list width instead
    /// (92% of it) is what used to push wide screenshots past the card and clip them on the right.
    /// </summary>
    private const double WidthFraction = 1.0;
    private const double HeightFraction = 0.75;
    private const double Floor = 96;

    /// <summary>The compact preview box used by <c>AppSettings.ImagePreview == "Medium"</c>.</summary>
    private const double MediumWidth = 160;
    private const double MediumHeight = 120;

    /// <summary>Small mode draws the image in the 40 DIP icon slot; this keeps the converter honest
    /// if it is ever asked for a size in that mode.</summary>
    private const double SmallSize = 40;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return double.NaN;
        }

        var scale = ToLength(values[1], 1.0);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var width = bitmap.PixelWidth / scale;
        var height = bitmap.PixelHeight / scale;

        var contentWidth = ToLength(values[2], 0);
        var listHeight = ToLength(values[3], 0);
        var mode = values.Length > 4 ? values[4] as string : null;

        if (string.Equals(mode, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            // Compact mode: a fixed box so every image row has the same rhythm. The content width
            // is still respected, otherwise a narrow panel would push the preview past the card.
            var capWidth = contentWidth > 0 ? Math.Min(MediumWidth, contentWidth) : MediumWidth;
            var factor = Math.Min(1.0, Math.Min(capWidth / width, MediumHeight / height));
            width *= factor;
            height *= factor;
        }
        else if (string.Equals(mode, "Small", StringComparison.OrdinalIgnoreCase))
        {
            var factor = Math.Min(1.0, SmallSize / Math.Max(width, height));
            width *= factor;
            height *= factor;
        }
        else if (contentWidth > 0 && listHeight > 0)
        {
            var capWidth = Math.Max(Floor, contentWidth * WidthFraction);
            var capHeight = Math.Max(Floor, listHeight * HeightFraction);
            var factor = Math.Min(1.0, Math.Min(capWidth / width, capHeight / height));
            width *= factor;
            height *= factor;
        }

        return parameter as string == "Height" ? height : width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToLength(object value, double fallback)
        => value is double number && !double.IsNaN(number) && number > 0 ? number : fallback;
}
