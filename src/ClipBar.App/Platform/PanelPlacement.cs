namespace ClipBar.App.Platform;

/// <summary>
/// Pure placement math for the panel: centre a window on the monitor under the cursor.
/// </summary>
/// <remarks>
/// Monitor work areas are physical pixels while WPF sizes windows in DIPs. DIPs are
/// device-independent, so a 420 DIP panel is still 420 DIP on a 200% monitor — only the
/// <em>target</em> monitor's scale converts it to the physical pixels that are centred there.
/// Using the current monitor's scale instead (the pre-1.1.2 rule) made the panel land
/// off-centre whenever it moved between monitors with different DPI (F-23); keeping the
/// arithmetic here makes that measurable without a live second monitor.
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
    /// <param name="width">Window width in DIPs (DPI-independent).</param>
    /// <param name="height">Window height in DIPs (DPI-independent).</param>
    /// <param name="targetScale">Scale factor of the monitor the panel will appear on.</param>
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
        double targetScale)
    {
        var target = Sanitize(targetScale);

        // WPF DIPs are device-independent: the same panel is 420 DIP on every monitor. Only the
        // destination monitor's scale turns that into the physical footprint being centred.
        var physicalWidth = Math.Max(0, width) * target;
        var physicalHeight = Math.Max(0, height) * target;

        // Clamp to the work area: a window larger than the monitor would otherwise get a
        // negative offset and land partly off-screen.
        return (
            (workLeft + Math.Max(0, (workWidth - physicalWidth) / 2)) / target,
            (workTop + Math.Max(0, (workHeight - physicalHeight) / 2)) / target);
    }

    /// <summary>A scale factor is never legitimately zero or negative; treat those as 100%.</summary>
    private static double Sanitize(double scale)
        => scale > 0 && double.IsFinite(scale) ? scale : 1.0;

    /// <summary>
    /// DIP top-left for a panel opened next to the cursor: per axis, prefer the natural reading
    /// direction (right / below) and flip to left / above only when the panel does not fit that
    /// way but fits on the other side. When it fits neither, keep the preferred side — the final
    /// clamp pulls the panel back inside the work area instead of pushing it off-screen.
    /// </summary>
    /// <param name="cursorX">Cursor X, physical pixels (captured at the show request).</param>
    /// <param name="cursorY">Cursor Y, physical pixels.</param>
    /// <param name="workLeft">Work area left edge, physical pixels.</param>
    /// <param name="workTop">Work area top edge, physical pixels.</param>
    /// <param name="workWidth">Work area width, physical pixels.</param>
    /// <param name="workHeight">Work area height, physical pixels.</param>
    /// <param name="width">Panel width in DIPs.</param>
    /// <param name="height">Panel height in DIPs.</param>
    /// <param name="targetScale">Scale of the monitor the cursor is on.</param>
    public static (double Left, double Top) PlaceNearCursor(
        int cursorX,
        int cursorY,
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        double width,
        double height,
        double targetScale)
    {
        const int anchorGap = 6; // physical px between the hot pixel and the panel edge

        var s = Sanitize(targetScale);
        var physicalWidth = Math.Max(0, width) * s;
        var physicalHeight = Math.Max(0, height) * s;

        var rightSpace = workLeft + workWidth - cursorX;
        var leftSpace = cursorX - workLeft;
        var belowSpace = workTop + workHeight - cursorY;
        var aboveSpace = cursorY - workTop;

        // Flip only when the preferred side cannot hold the panel and the other side can; when
        // both fit (or neither does) the natural direction wins, so the panel lands predictably.
        var toRight = rightSpace >= physicalWidth || rightSpace >= leftSpace;
        var below = belowSpace >= physicalHeight || belowSpace >= aboveSpace;

        var leftPx = toRight ? cursorX + anchorGap : cursorX - physicalWidth - anchorGap;
        var topPx = below ? cursorY + anchorGap : cursorY - physicalHeight - anchorGap;

        // Clamp to the work area so a panel larger than the remaining space is pulled back
        // inside instead of hanging off-screen; the origin is respected for negative-X monitors.
        var maxLeft = Math.Max(workLeft, workLeft + workWidth - physicalWidth);
        var maxTop = Math.Max(workTop, workTop + workHeight - physicalHeight);
        leftPx = Math.Clamp(leftPx, workLeft, maxLeft);
        topPx = Math.Clamp(topPx, workTop, maxTop);

        return (leftPx / s, topPx / s);
    }
}
