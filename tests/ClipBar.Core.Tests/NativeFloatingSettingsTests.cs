using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

/// <summary>
/// Settings for ClipBar's own floating clipboard bar (hold/drag gestures). The listener is a
/// global input sink, so it must be opt-in and every timing must survive a hand-edited file.
/// </summary>
public class NativeFloatingSettingsTests
{
    [Fact]
    public void TheFeatureIsOffByDefault()
    {
        Assert.False(new AppSettings().NativeFloatingEnabled);
    }

    [Fact]
    public void OutOfRangeTimingsClampToTheSupportedRange()
    {
        var settings = new AppSettings
        {
            NativeFloatingEnabled = true,
            HoldDurationMs = 5,
            DragThresholdPx = 0,
            HoverExpandMs = -20,
            FloatingAutoHideMs = 5,
        }.Normalize();

        Assert.True(settings.NativeFloatingEnabled);
        // A hand-edited file is clamped into the range, never left at a value the gesture code
        // cannot honour (a 5 ms hold would fire on every click).
        Assert.Equal(200, settings.HoldDurationMs);
        Assert.Equal(15, settings.DragThresholdPx);
        Assert.Equal(200, settings.HoverExpandMs);
        Assert.Equal(300, settings.FloatingAutoHideMs);
    }

    [Fact]
    public void AbsurdTimingsClampToTheirCeilings()
    {
        var settings = new AppSettings
        {
            HoldDurationMs = 99999,
            DragThresholdPx = 99999,
            HoverExpandMs = 99999,
            FloatingAutoHideMs = 99999,
        }.Normalize();

        Assert.Equal(2000, settings.HoldDurationMs);
        Assert.Equal(100, settings.DragThresholdPx);
        Assert.Equal(2000, settings.HoverExpandMs);
        Assert.Equal(10000, settings.FloatingAutoHideMs);
    }

    [Fact]
    public void CloneKeepsTheFloatingSettings()
    {
        var clone = new AppSettings
        {
            NativeFloatingEnabled = true,
            HoldDurationMs = 800,
            DragThresholdPx = 24,
            HoverExpandMs = 120,
            FloatingAutoHideMs = 1500,
            InvokeOnDrag = false,
        }.Clone();

        Assert.True(clone.NativeFloatingEnabled);
        Assert.Equal(800, clone.HoldDurationMs);
        Assert.Equal(24, clone.DragThresholdPx);
        Assert.Equal(120, clone.HoverExpandMs);
        Assert.Equal(1500, clone.FloatingAutoHideMs);
        Assert.False(clone.InvokeOnDrag);
    }
}
