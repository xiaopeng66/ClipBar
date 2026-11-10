using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ClipBar.App;

/// <summary>
/// Decides whether a window-level preview key event is headed for a text-editing control.
/// The panel's <c>PreviewKeyDown</c> tunnels from the window before the focused control sees
/// the key, so an unscoped Delete used to delete the selected history row while the user was
/// only editing the search query (F-01).
/// </summary>
public static class PanelKeyScoping
{
    /// <summary>
    /// True when the key event is headed for a text-editing control (the search box): history
    /// shortcuts must stand down and let the control handle the key natively. Both the routed
    /// source and the keyboard focus are checked, because a templated text host can report
    /// either as the event's origin.
    /// </summary>
    public static bool IsRoutedToTextEditor(object? originalSource, IInputElement? focusedElement)
        => originalSource is TextBoxBase || focusedElement is TextBoxBase;
}
