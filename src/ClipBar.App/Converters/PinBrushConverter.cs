using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClipBar.App.Converters;

public sealed class PinBrushConverter : IValueConverter
{
    // One frozen brush per state. The old code built and froze a new SolidColorBrush for every
    // pin icon on every binding pass — dozens per refresh, purely garbage once the list is
    // virtualised and rows are reused.
    private static readonly Brush PinnedBrush = Create(0x60, 0xA5, 0xFA);
    private static readonly Brush UnpinnedBrush = Create(0x8A, 0x8A, 0x8A);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? PinnedBrush : UnpinnedBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
