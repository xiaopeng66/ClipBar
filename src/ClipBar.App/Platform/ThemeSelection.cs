using Wpf.Ui.Appearance;

namespace ClipBar.App.Platform;

/// <summary>
/// Chooses the WPF-UI theme. Windows High Contrast always wins over the saved Light/Dark/System
/// preference so the application uses the high-contrast resource dictionary while the setting is
/// enabled, and returns to the saved preference when it is disabled.
/// </summary>
public static class ThemeSelection
{
    public static ApplicationTheme Resolve(
        string setting,
        bool highContrast,
        ApplicationTheme systemTheme)
    {
        if (highContrast)
        {
            return ApplicationTheme.HighContrast;
        }

        return setting switch
        {
            "Dark" => ApplicationTheme.Dark,
            "Light" => ApplicationTheme.Light,
            _ => systemTheme == ApplicationTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
    }

    /// <summary>
    /// The custom light/dark RGB ramps are contrast adjustments for the normal themes; they must
    /// not shadow the high-contrast dictionary.
    /// </summary>
    public static bool ShouldApplyCustomPalette(ApplicationTheme theme)
        => theme != ApplicationTheme.HighContrast;
}
