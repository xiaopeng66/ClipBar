using ClipBar.App.Platform;

namespace ClipBar.App.Tests;

/// <summary>
/// The panel is a transient popup: click-outside must dismiss it, but menus, dialogs and the
/// keep-open paste flow hold it open for their lifetime. These tests pin the deferred decision
/// that runs when the last suppression scope ends.
/// </summary>
public class PanelDismissPolicyTests
{
    [Fact]
    public void ADeactivationWhileSuppressedDismissesWhenAForeignWindowHasFocus()
    {
        Assert.True(PanelDismissPolicy.ShouldDismiss(
            deactivatedWhileSuppressed: true,
            isVisible: true,
            allowClose: false,
            foregroundBelongsToSelf: false));
    }

    [Fact]
    public void ClickingBackIntoThePanelKeepsItOpen()
    {
        // The menu closed by deactivation, but the click landed inside the panel: the panel owns
        // the foreground again, so only the menu was dismissed.
        Assert.False(PanelDismissPolicy.ShouldDismiss(
            deactivatedWhileSuppressed: true,
            isVisible: true,
            allowClose: false,
            foregroundBelongsToSelf: true));
    }

    [Fact]
    public void ADialogThatReturnsFocusKeepsThePanel()
    {
        // The dialog's finally block re-activates the panel before the scope ends.
        Assert.False(PanelDismissPolicy.ShouldDismiss(
            deactivatedWhileSuppressed: true,
            isVisible: true,
            allowClose: false,
            foregroundBelongsToSelf: true));
    }

    [Fact]
    public void NoDeactivationDuringSuppressionMeansNothingToDismiss()
    {
        Assert.False(PanelDismissPolicy.ShouldDismiss(
            deactivatedWhileSuppressed: false,
            isVisible: true,
            allowClose: false,
            foregroundBelongsToSelf: false));
    }

    [Fact]
    public void HidingOrExitingWinsOverTheDeferredCheck()
    {
        Assert.False(PanelDismissPolicy.ShouldDismiss(true, isVisible: false, allowClose: false, foregroundBelongsToSelf: false));
        Assert.False(PanelDismissPolicy.ShouldDismiss(true, isVisible: true, allowClose: true, foregroundBelongsToSelf: false));
    }
}
