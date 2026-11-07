using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// The native floating bar's gesture rules, mirroring SnipDo 3.0.88.0 (hold 600 ms still, or drag
/// at least 15 px): a press that stays put opens the bar, a press that moves becomes a selection
/// drag, and a short click does nothing.
/// </summary>
public class FloatingGestureStateMachineTests
{
    private static FloatingGestureStateMachine NewMachine(int hold = 600, int threshold = 15)
        => new(hold, threshold);

    [Fact]
    public void HoldingStillForTheHoldDurationAsksForTheBar()
    {
        var machine = NewMachine();

        machine.Down(100, 100, nowMs: 0);
        Assert.Equal(GestureAction.None, machine.Tick(100, 100, 500));
        Assert.Equal(GestureAction.ShowBarFromHold, machine.Tick(100, 100, 600));

        // Only once: later ticks must not queue a second bar.
        Assert.Equal(GestureAction.None, machine.Tick(100, 100, 700));
    }

    [Fact]
    public void JitterInsideTheDragThresholdStillCountsAsHolding()
    {
        var machine = NewMachine(threshold: 15);

        machine.Down(100, 100, 0);
        Assert.False(machine.Move(110, 112, 200));
        Assert.Equal(GestureAction.ShowBarFromHold, machine.Tick(110, 112, 600));
    }

    [Fact]
    public void MovingPastTheThresholdTurnsThePressIntoADragAndCancelsTheHold()
    {
        var machine = NewMachine();

        machine.Down(100, 100, 0);
        Assert.True(machine.Move(120, 100, 100));
        Assert.Equal("Dragging", machine.StateName);

        // The hold must no longer fire after the press became a drag.
        Assert.Equal(GestureAction.None, machine.Tick(120, 100, 900));
    }

    [Fact]
    public void ARealDragSelectAsksForASelectionProbeOnRelease()
    {
        var machine = NewMachine();

        machine.Down(100, 100, 0);
        machine.Move(140, 100, 100);
        var action = machine.Up(400, out _, out _);

        Assert.Equal(GestureAction.ProbeSelection, action);
    }

    [Fact]
    public void AVeryShortDragIsTreatedAsANormalClick()
    {
        var machine = NewMachine();

        machine.Down(100, 100, 0);
        machine.Move(140, 100, 100);

        // Released only 50 ms after the drag started: a flick, not a selection.
        Assert.Equal(GestureAction.None, machine.Up(150, out _, out _));
    }

    [Fact]
    public void AQuickClickNeitherHoldsNorProbes()
    {
        var machine = NewMachine();

        machine.Down(50, 50, 0);
        Assert.Equal(GestureAction.None, machine.Up(120, out _, out _));
        Assert.Equal("Idle", machine.StateName);
    }

    [Fact]
    public void ReleasingAfterAHoldDoesNotRequestASelectionProbe()
    {
        var machine = NewMachine();

        machine.Down(10, 10, 0);
        Assert.Equal(GestureAction.ShowBarFromHold, machine.Tick(10, 10, 600));
        Assert.Equal(GestureAction.None, machine.Up(650, out _, out _));
    }

    [Fact]
    public void ResetDropsAnInFlightGesture()
    {
        var machine = NewMachine();

        machine.Down(10, 10, 0);
        machine.Reset();

        Assert.False(machine.IsActive);
        Assert.Equal(GestureAction.None, machine.Tick(10, 10, 5000));
    }

    [Fact]
    public void ADragReportsTheAnchorItStartedFrom()
    {
        var machine = NewMachine();

        machine.Down(300, 200, 0);
        machine.Move(340, 210, 100);
        machine.Up(500, out var x, out var y);

        Assert.Equal(300, x);
        Assert.Equal(200, y);
    }
}
