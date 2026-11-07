namespace ClipBar.App.Services;

/// <summary>
/// What the gesture recogniser wants the shell to do next.
/// </summary>
public enum GestureAction
{
    None,

    /// <summary>The left button was held still long enough: show the floating bar at this point.</summary>
    ShowBarFromHold,

    /// <summary>A drag-select ended: ask the selection probe, then maybe show the bar.</summary>
    ProbeSelection,
}

/// <summary>
/// Pure gesture state machine for the native floating clipboard bar.
/// <para>
/// It mirrors what SnipDo 3.0.88.0 does with Raw Input (see docs/PLAN-NATIVE-FLOATING-CLIPBOARD.md):
/// a left press that stays put for the hold duration opens the bar, while a press that moves past
/// the drag threshold becomes a selection drag whose release asks for the selected text.
/// </para>
/// <para>
/// Deliberately free of Windows, WPF and timers: the caller feeds it raw coordinates and a
/// monotonic clock, which makes every rule testable without a desktop.
/// </para>
/// </summary>
public sealed class FloatingGestureStateMachine
{
    private enum State
    {
        Idle,
        Pressed,
        Dragging,
        HoldFired,
    }

    private const int DragSelectMinimumMs = 200;

    private readonly int _holdDurationMs;
    private readonly int _dragThresholdPx;

    private State _state = State.Idle;
    private int _downX;
    private int _downY;
    private long _downAtMs;
    private long _dragStartedAtMs;

    public FloatingGestureStateMachine(int holdDurationMs, int dragThresholdPx)
    {
        _holdDurationMs = Math.Max(50, holdDurationMs);
        _dragThresholdPx = Math.Max(1, dragThresholdPx);
    }

    /// <summary>Current state name, for tests and diagnostics.</summary>
    public string StateName => _state.ToString();

    /// <summary>Left button pressed at <paramref name="x"/>,<paramref name="y"/>.</summary>
    public void Down(int x, int y, long nowMs)
    {
        _state = State.Pressed;
        _downX = x;
        _downY = y;
        _downAtMs = nowMs;
        _dragStartedAtMs = 0;
    }

    /// <summary>
    /// Pointer moved. Returns <c>true</c> the moment the press turns into a drag, so the caller
    /// can cancel a pending hold.
    /// </summary>
    public bool Move(int x, int y, long nowMs)
    {
        if (_state != State.Pressed)
        {
            return false;
        }

        if (Math.Abs(x - _downX) <= _dragThresholdPx && Math.Abs(y - _downY) <= _dragThresholdPx)
        {
            return false;
        }

        _state = State.Dragging;
        _dragStartedAtMs = nowMs;
        return true;
    }

    /// <summary>
    /// Timer tick while the button is still down. Returns <see cref="GestureAction.ShowBarFromHold"/>
    /// once, when the press has stayed inside the drag threshold for the hold duration.
    /// <paramref name="x"/>/<paramref name="y"/> are the latest pointer position, used as the anchor.
    /// </summary>
    public GestureAction Tick(int x, int y, long nowMs)
    {
        if (_state != State.Pressed)
        {
            return GestureAction.None;
        }

        if (nowMs - _downAtMs < _holdDurationMs)
        {
            return GestureAction.None;
        }

        // Held still long enough. Keep the press recorded so the eventual Up is consumed here
        // instead of being mistaken for a click.
        _state = State.HoldFired;
        _downX = x;
        _downY = y;
        return GestureAction.ShowBarFromHold;
    }

    /// <summary>
    /// Left button released. Returns <see cref="GestureAction.ProbeSelection"/> for a real
    /// drag-select (moved past the threshold and held long enough), otherwise
    /// <see cref="GestureAction.None"/>.
    /// </summary>
    public GestureAction Up(long nowMs, out int x, out int y)
    {
        var action = GestureAction.None;
        if (_state == State.Dragging && nowMs - _dragStartedAtMs >= DragSelectMinimumMs)
        {
            action = GestureAction.ProbeSelection;
        }

        x = _downX;
        y = _downY;
        _state = State.Idle;
        return action;
    }

    /// <summary>Drops any in-flight gesture (disabled, target changed, excluded app).</summary>
    public void Reset() => _state = State.Idle;

    /// <summary>True while a press or drag is in flight.</summary>
    public bool IsActive => _state != State.Idle;
}
