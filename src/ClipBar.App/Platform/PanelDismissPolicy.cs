namespace ClipBar.App.Platform;

/// <summary>
/// The panel is a transient popup: losing activation normally hides it. Menus, dialogs and the
/// keep-open paste flow suppress that rule for their lifetime, so the decision is deferred until
/// the last suppression ends.
/// </summary>
/// <remarks>
/// The deferred check must run a dispatcher turn later: when a menu closes because the user
/// clicked inside the panel, the menu-deactivation notification and the panel activation can
/// arrive in either order. It must judge by the <em>foreground window</em>, not by
/// <c>Window.IsActive</c>: a closing owned menu makes WPF mark its owner active even when the
/// click that closed it landed on another application, so IsActive alone would keep the panel.
/// </remarks>
public static class PanelDismissPolicy
{
    /// <summary>
    /// True when the panel should hide: it lost activation while suppressed, it is still shown,
    /// the app is not exiting, and the interaction that ended the suppression left a foreign
    /// window in the foreground (the user moved on). A foreground window belonging to this
    /// process — the panel itself, a dialog, the next menu — keeps the panel.
    /// </summary>
    public static bool ShouldDismiss(
        bool deactivatedWhileSuppressed,
        bool isVisible,
        bool allowClose,
        bool foregroundBelongsToSelf,
        bool holdOpen = false)
        => !holdOpen
           && deactivatedWhileSuppressed
           && isVisible
           && !allowClose
           && !foregroundBelongsToSelf;
}
