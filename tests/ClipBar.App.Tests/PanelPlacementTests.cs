using ClipBar.App.Platform;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-44: the panel is placed on the monitor under the cursor, and monitor rects are
/// physical pixels while WPF positions windows in DIPs. These tests pin the arithmetic that used
/// to drift across mixed-DPI setups (F-23) and the degenerate inputs that must not produce NaN
/// or off-screen coordinates. On-screen centring on a real second monitor stays a semi-manual
/// check, per plan §5.3.
/// </summary>
public class PanelPlacementTests
{
    [Fact]
    public void ACentredWindowOnThePrimaryMonitorStaysAt100Percent()
    {
        var (left, top) = PanelPlacement.CenterIn(
            0, 0, 1920, 1080,
            420, 620,
            currentScale: 1.0,
            targetScale: 1.0);

        Assert.Equal((1920 - 420) / 2.0, left);
        Assert.Equal((1080 - 620) / 2.0, top);
    }

    [Fact]
    public void TheWindowSizeIsConvertedWithTheCurrentScaleBeforeDividingByTheTarget()
    {
        // F-23's rule: size in physical pixels uses the *current* monitor's scale, and the
        // result is expressed in the *target* monitor's DIPs. Both scales are applied, so a
        // 420 DIP panel measured at 150% occupies 630 physical px when centring.
        var (left, top) = PanelPlacement.CenterIn(
            1920, 0, 2560, 1620,
            420, 620,
            currentScale: 1.5,
            targetScale: 1.5);

        Assert.Equal((1920 + (2560 - 630) / 2.0) / 1.5, left);
        Assert.Equal(((1620 - 930) / 2.0) / 1.5, top);
    }

    [Fact]
    public void MovingBetweenDifferentScalesAppliesEachScaleOnce()
    {
        // Window measured at 100% (420 physical px) placed on a 150% monitor: the physical
        // offset is computed at 420, then expressed in that monitor's DIPs.
        var (left, top) = PanelPlacement.CenterIn(
            1920, 0, 2560, 1620,
            420, 620,
            currentScale: 1.0,
            targetScale: 1.5);

        Assert.Equal((1920 + (2560 - 420) / 2.0) / 1.5, left);
        Assert.Equal(((1620 - 620) / 2.0) / 1.5, top);

        // The panel must land inside the target work area, not off its right edge.
        Assert.True(left > 1920.0 / 1.5);
        Assert.True(left < (1920 + 2560) / 1.5);
    }

    [Fact]
    public void AWindowLargerThanTheMonitorIsClampedToItsTopLeft()
    {
        // A tiny work area (or a huge panel) must not produce a negative offset that pushes the
        // panel partly off-screen.
        var (left, top) = PanelPlacement.CenterIn(0, 0, 300, 200, 420, 620, 1.0, 1.0);

        Assert.Equal(0.0, left);
        Assert.Equal(0.0, top);
    }

    [Fact]
    public void AZeroOrNegativeScaleFallsBackTo100Percent()
    {
        // DpiScale is 0 before the window is first measured; dividing by it used to be a way to
        // get Infinity/NaN window coordinates.
        var (left, top) = PanelPlacement.CenterIn(0, 0, 1920, 1080, 420, 620, 0.0, 0.0);

        Assert.Equal((1920 - 420) / 2.0, left);
        Assert.Equal((1080 - 620) / 2.0, top);
        Assert.True(double.IsFinite(left) && double.IsFinite(top));
    }

    [Fact]
    public void MonitorOriginsAreRespectedForASecondaryScreenLeftOfThePrimary()
    {
        // A monitor at negative X (common when it is placed left of the primary) must not be
        // treated as if it started at 0.
        var (left, top) = PanelPlacement.CenterIn(-2560, 0, 2560, 1440, 420, 620, 1.0, 1.0);

        Assert.Equal(-2560 + (2560 - 420) / 2.0, left);
        Assert.Equal((1440 - 620) / 2.0, top);
    }
}
