using System.Windows.Input;

namespace ClipBar.App.Platform;

/// <summary>
/// Keyboard routing for the main panel. The window-level tunnel runs before the focused control,
/// so it must leave composition keys and focused buttons to their owners instead of applying the
/// panel shortcuts.
/// </summary>
public static class PanelKeyRouting
{
    /// <summary>
    /// True when the key belongs to the IME or to an active composition. WPF normally reports
    /// composition keys as <see cref="Key.ImeProcessed"/>; the composition flag covers IMEs that
    /// deliver commit/cancel keys as ordinary keys while the composition is still open.
    /// </summary>
    public static bool ShouldLetImeHandle(Key key, bool compositionActive)
        => key == Key.ImeProcessed
           || (compositionActive && key is Key.Enter or Key.Escape or Key.Up or Key.Down);

    /// <summary>
    /// Enter pastes the selected row only when no composition is active and the focused control
    /// is not a button. A focused button must receive Enter itself (invoke/toggle) rather than
    /// having the window tunnel consume it.
    /// </summary>
    public static bool ShouldPasteOnEnter(bool compositionActive, bool focusInsideButton)
        => !compositionActive && !focusInsideButton;

    public static bool IsCompositionCancellation(Key key, Key imeProcessedKey, bool compositionActive)
        => compositionActive
           && (key == Key.Escape || (key == Key.ImeProcessed && imeProcessedKey == Key.Escape));
}
