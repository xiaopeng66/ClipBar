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
            targetScale: 1.0);

        Assert.Equal((1920 - 420) / 2.0, left);
        Assert.Equal((1080 - 620) / 2.0, top);
    }

    [Fact]
    public void AWindowMeasuredAtAnotherScaleIsSizedForTheDestinationMonitor()
    {
        // F-23 regression: 420 DIP stays 420 DIP on every monitor. On a 150% target it occupies
        // 630 physical px, no matter which scale the window was measured under before moving.
        var (left, top) = PanelPlacement.CenterIn(
            1920, 0, 2560, 1620,
            420, 620,
            targetScale: 1.5);

        Assert.Equal((1920 + (2560 - 630) / 2.0) / 1.5, left);
        Assert.Equal(((1620 - 930) / 2.0) / 1.5, top);
    }

    [Fact]
    public void A125To200PercentMoveStillCentresThePanel()
    {
        // The measured desk layout: primary 2560x1440 at 125%, portrait 2160x3840 at 200% to
        // the left. A 420x620 DIP panel on the portrait monitor must be 840x1240 physical px and
        // centred there, not sized with the 125% scale it came from.
        var (left, top) = PanelPlacement.CenterIn(
            -2160, -787, 2160, 3840,
            420, 620,
            targetScale: 2.0);

        Assert.Equal(-750.0, left);
        Assert.Equal(256.5, top);
    }

    [Fact]
    public void AWindowLargerThanTheMonitorIsClampedToItsTopLeft()
    {
        // A tiny work area (or a huge panel) must not produce a negative offset that pushes the
        // panel partly off-screen.
        var (left, top) = PanelPlacement.CenterIn(0, 0, 300, 200, 420, 620, 1.0);

        Assert.Equal(0.0, left);
        Assert.Equal(0.0, top);
    }

    [Fact]
    public void AZeroOrNegativeScaleFallsBackTo100Percent()
    {
        // DpiScale is 0 before the window is first measured; dividing by it used to be a way to
        // get Infinity/NaN window coordinates.
        var (left, top) = PanelPlacement.CenterIn(0, 0, 1920, 1080, 420, 620, 0.0);

        Assert.Equal((1920 - 420) / 2.0, left);
        Assert.Equal((1080 - 620) / 2.0, top);
        Assert.True(double.IsFinite(left) && double.IsFinite(top));
    }

    [Fact]
    public void ClampInKeepsASavedPositionInsideTheWorkArea()
    {
        // Saved at 10,10 on a 125% monitor: the window must not sit at the very edge, but it is
        // legal to be inside; a position beyond the right/bottom edge is pulled back.
        var (left, top) = PanelPlacement.ClampIn(5000, 5000, 420, 620, 0, 0, 2560, 1440, 1.25);
        Assert.Equal(2560 / 1.25 - 420, left, 3);
        Assert.Equal(1440 / 1.25 - 620, top, 3);
    }

    [Fact]
    public void ClampInPullsANegativePositionToTheWorkAreaOrigin()
    {
        var (left, top) = PanelPlacement.ClampIn(-300, -200, 420, 620, 0, 0, 1920, 1080, 1.0);
        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void ClampInHandlesAWindowWiderThanTheWorkArea()
    {
        // Wider than the work area: pin to the left edge instead of producing left > right.
        var (left, _) = PanelPlacement.ClampIn(100, 100, 5000, 620, 0, 0, 1920, 1080, 1.0);
        Assert.Equal(0, left);
    }

    [Fact]
    public void MonitorOriginsAreRespectedForASecondaryScreenLeftOfThePrimary()
    {
        // A monitor at negative X (common when it is placed left of the primary) must not be
        // treated as if it started at 0.
        var (left, top) = PanelPlacement.CenterIn(-2560, 0, 2560, 1440, 420, 620, 1.0);

        Assert.Equal(-2560 + (2560 - 420) / 2.0, left);
        Assert.Equal((1440 - 620) / 2.0, top);
    }

    // ---- CursorAnchor: pointer-anchored placement with per-axis flipping -----------------

    [Fact]
    public void CursorAnchorOpensDownRightWhenBothDirectionsHaveRoom()
    {
        var (left, top) = PanelPlacement.PlaceNearCursor(300, 300, 0, 0, 1920, 1080, 420, 620, 1.0);

        Assert.Equal(306, left, 3); // cursor + 6 px gap
        Assert.Equal(306, top, 3);
    }

    [Fact]
    public void CursorAnchorFlipsUpWhenTheBottomCannotHoldThePanel()
    {
        // Cursor 80 px above the bottom edge: below cannot fit 620, above can.
        var (left, top) = PanelPlacement.PlaceNearCursor(300, 1000, 0, 0, 1920, 1080, 420, 620, 1.0);

        Assert.Equal(306, left, 3);
        Assert.Equal(1000 - 620 - 6, top, 3);
    }

    [Fact]
    public void CursorAnchorFlipsLeftWhenTheRightCannotHoldThePanel()
    {
        var (left, top) = PanelPlacement.PlaceNearCursor(1800, 300, 0, 0, 1920, 1080, 420, 620, 1.0);

        Assert.Equal(1800 - 420 - 6, left, 3);
        Assert.Equal(306, top, 3);
    }

    [Fact]
    public void CursorAnchorFlipsBothAxesInTheBottomRightCorner()
    {
        var (left, top) = PanelPlacement.PlaceNearCursor(1850, 1020, 0, 0, 1920, 1080, 420, 620, 1.0);

        Assert.Equal(1850 - 420 - 6, left, 3);
        Assert.Equal(1020 - 620 - 6, top, 3);
    }

    [Fact]
    public void CursorAnchorClampsInsideWhenThePanelIsTallerThanTheWorkArea()
    {
        // Neither vertical side fits (panel 620 > work 400): the flip comparison picks "above",
        // and the clamp pulls the panel to the top edge instead of leaving it off-screen.
        var (left, top) = PanelPlacement.PlaceNearCursor(300, 300, 0, 0, 1920, 400, 420, 620, 1.0);

        Assert.Equal(306, left, 3);
        Assert.Equal(0, top, 3);
    }

    [Fact]
    public void CursorAnchorRespectsTheTargetMonitorScale()
    {
        // 200% monitor: 420x620 DIP is 840x1240 px; the result comes back in that monitor's DIPs.
        var (left, top) = PanelPlacement.PlaceNearCursor(100, 100, 0, 0, 2160, 3840, 420, 620, 2.0);

        Assert.Equal(53.0, left, 3); // (100 + 6) / 2
        Assert.Equal(53.0, top, 3);
    }

    [Fact]
    public void CursorAnchorRespectsANegativeOriginMonitor()
    {
        // The measured desk layout: portrait 200% monitor at (-2160,-787). The cursor sits near
        // its top-left corner with room to the right and below.
        var (left, top) = PanelPlacement.PlaceNearCursor(-2100, -700, -2160, -787, 2160, 3840, 420, 620, 2.0);

        Assert.Equal((-2100 + 6) / 2.0, left, 3);
        Assert.Equal((-700 + 6) / 2.0, top, 3);
    }
}
