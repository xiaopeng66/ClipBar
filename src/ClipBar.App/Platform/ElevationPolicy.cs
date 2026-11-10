namespace ClipBar.App.Platform;

/// <summary>
/// Whether an automatic Ctrl+V must be withheld for the target's integrity level. Copying to the
/// clipboard is a separate operation: it can succeed even when UIPI prevents input injection.
/// </summary>
public static class ElevationPolicy
{
    public static bool MustWithholdPaste(bool targetElevated, bool selfElevated)
        => targetElevated && !selfElevated;
}
