using System.Windows;
using System.Windows.Media;

namespace ClipBar.App.Platform;

/// <summary>
/// Whether the pointer is inside an element's on-screen rectangle at this moment.
/// <para>
/// WPF's ButtonBase captures the mouse on press and raises <c>Click</c> on release wherever the
/// pointer happens to be, so pressing a row, dragging off it and letting go still counted as a
/// click: the compact list pasted a row the user had deliberately dragged away from. A release
/// only counts as a click when it lands on the element.
/// </para>
/// <para>
/// The comparison is in physical pixels via <see cref="UIElement.PointToScreen"/>, because
/// <c>IsMouseOver</c> stays true for the captured element and would answer the wrong question.
/// </para>
/// </summary>
internal static class PointerInside
{
    public static bool Element(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

            // A transform can be degenerate mid-layout; normalise so the comparison holds.
            var left = Math.Min(topLeft.X, bottomRight.X);
            var right = Math.Max(topLeft.X, bottomRight.X);
            var top = Math.Min(topLeft.Y, bottomRight.Y);
            var bottom = Math.Max(topLeft.Y, bottomRight.Y);

            return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
        }
        catch (InvalidOperationException)
        {
            // No presentation source yet: nothing is on screen to click.
            return false;
        }
    }
}
