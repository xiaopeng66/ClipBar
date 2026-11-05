namespace ClipBar.App.Platform;

/// <summary>
/// Pure placement math for the panel: centre a window on the monitor under the cursor.
/// </summary>
/// <remarks>
/// Monitor work areas are physical pixels while WPF positions windows in the DIPs of the
/// window's <em>own</em> DPI. Centring therefore has to convert the window size to physical
/// pixels with the current scale, centre in that space, and only then divide by the target
/// monitor's scale. Mixing the two is what made the panel drift off-centre on mixed-DPI
/// setups (plan §5.3 T-44), and keeping this out of the window class makes that arithmetic
/// testable without a live second monitor.
/// </remarks>
public static class PanelPlacement
{
    /// <summary>
    /// DIP top-left that centres a <paramref name="width"/>×<paramref name="height"/> window
    /// (already in DIPs) inside a work area given in physical pixels.
    /// </summary>
    /// <param name="workLeft">Work area left edge, physical pixels.</param>
    /// <param name="workTop">Work area top edge, physical pixels.</param>
    /// <param name="workWidth">Work area width, physical pixels.</param>
    /// <param name="workHeight">Work area height, physical pixels.</param>
    /// <param name="width">Window width in DIPs of the current monitor.</param>
    /// <param name="height">Window height in DIPs of the current monitor.</param>
    /// <param name="currentScale">Scale factor of the monitor the window is currently on.</param>
    /// <param name="targetScale">Scale factor of the monitor the panel will move to.</param>
    /// <summary>
    /// Clamps a saved window position so the whole window stays inside the work area (A5).
    /// Position and sizes are DIPs; the work area is physical pixels.
    /// </summary>
    public static (double Left, double Top) ClampIn(
        double left,
        double top,
        double width,
        double height,
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        double scale)
    {
        var s = Sanitize(scale);
        var wl = workLeft / s;
        var wt = workTop / s;
        var wr = (workLeft + workWidth) / s;
        var wb = (workTop + workHeight) / s;

        // When the window is wider than the work area, keep it at the left edge; clamping must
        // never produce a lower bound above the upper bound.
        var maxLeft = Math.Max(wl, wr - width);
        var maxTop = Math.Max(wt, wb - height);
        return (Math.Clamp(left, wl, maxLeft), Math.Clamp(top, wt, maxTop));
    }

    public static (double Left, double Top) CenterIn(
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        double width,
        double height,
        double currentScale,
        double targetScale)
    {
        var current = Sanitize(currentScale);
        var target = Sanitize(targetScale);

        var physicalWidth = Math.Max(0, width) * current;
        var physicalHeight = Math.Max(0, height) * current;

        // Clamp to the work area: a window larger than the monitor would otherwise get a
        // negative offset and land partly off-screen.
        return (
            (workLeft + Math.Max(0, (workWidth - physicalWidth) / 2)) / target,
            (workTop + Math.Max(0, (workHeight - physicalHeight) / 2)) / target);
    }

    /// <summary>A scale factor is never legitimately zero or negative; treat those as 100%.</summary>
    private static double Sanitize(double scale)
        => scale > 0 && double.IsFinite(scale) ? scale : 1.0;
}
