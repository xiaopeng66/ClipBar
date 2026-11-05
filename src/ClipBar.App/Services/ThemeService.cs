using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClipBar.App.Services;

public static class ThemeService
{
    /// <summary>
    /// Light-mode replacements for the Fluent brushes that are too faint to read on a bright
    /// background: 6% black strokes and 45% black tertiary text nearly disappear. The values are
    /// pushed into Application.Resources, which shadows the merged theme dictionary, so every
    /// DynamicResource consumer (panel, tray menu, settings, dialogs) picks them up.
    /// </summary>
    /// <remarks>
    /// The text ramp is deliberately much darker than the Fluent defaults. Small text (11-12 px)
    /// is rendered with ClearType, and a glyph stem only covers roughly two thirds of its pixel,
    /// so a nominal 62% black secondary actually lands near 40% on screen — the "everything looks
    /// washed out" the light theme used to have. Note that secondary used to be set to 36%, i.e.
    /// *lighter* than tertiary, which inverted the whole hierarchy.
    /// </remarks>
    private static readonly (string Key, Color Color)[] LightOverrides =
    [
        // Strokes.
        ("ControlStrokeColorDefaultBrush", Color.FromArgb(0x2E, 0, 0, 0)),
        ("ControlStrokeColorSecondaryBrush", Color.FromArgb(0x3D, 0, 0, 0)),
        ("ControlStrokeColorTertiaryBrush", Color.FromArgb(0x26, 0, 0, 0)),
        ("SurfaceStrokeColorDefaultBrush", Color.FromArgb(0x26, 0, 0, 0)),
        ("DividerStrokeColorDefaultBrush", Color.FromArgb(0x33, 0, 0, 0)),
        ("ControlStrongFillColorDefaultBrush", Color.FromArgb(0x66, 0, 0, 0)),

        // Text ramp: primary 90%, secondary 82%, tertiary 75% black. ClearType renders a glyph
        // stem at roughly 70-75% of its nominal alpha, so 75% is what actually lands near the
        // 4.5:1 floor for the 11 px hint text.
        ("TextFillColorPrimaryBrush", Color.FromArgb(0xE6, 0, 0, 0)),
        ("TextFillColorSecondaryBrush", Color.FromArgb(0xD2, 0, 0, 0)),
        ("TextFillColorTertiaryBrush", Color.FromArgb(0xC0, 0, 0, 0)),
        ("TextPlaceholderColorBrush", Color.FromArgb(0xC0, 0, 0, 0)),
        ("TextFillColorDisabledBrush", Color.FromArgb(0x7A, 0, 0, 0)),
    ];

    /// <summary>
    /// Dark-mode text ramp. The Fluent defaults (77% / 55% white) are the mirror image of the
    /// light-mode problem: on the acrylic panel the 11 px subtitles and footer hints read as grey
    /// mush. Brightness is raised so the same three levels stay legible, again keeping the
    /// ordering primary &gt; secondary &gt; tertiary.
    /// </summary>
    private static readonly (string Key, Color Color)[] DarkOverrides =
    [
        ("TextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ("TextFillColorSecondaryBrush", Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
        ("TextFillColorTertiaryBrush", Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
        ("TextPlaceholderColorBrush", Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
        ("TextFillColorDisabledBrush", Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
    ];

    /// <summary>
    /// The theme currently pushed into the resource dictionaries, or <c>null</c> before the first
    /// apply. Kept so a repeated apply of the same theme can be skipped.
    /// </summary>
    private static ApplicationTheme? _applied;

    /// <summary>
    /// Applies <paramref name="setting"/> ("System", "Light" or "Dark"). Calling it again with the
    /// theme that is already active is a no-op: WPF-UI's <c>ApplicationThemeManager.Apply</c>
    /// rebuilds every window's background effect (<c>WindowBackgroundManager.UpdateBackground</c>
    /// removes the backdrop, removes the window background, then re-applies both), and doing that
    /// to an acrylic window that is already showing leaves the panel painted with a flat, opaque
    /// background instead of acrylic. 保存/应用 used to run this unconditionally, so pressing 应用
    /// with an unchanged theme turned the panel white.
    /// </summary>
    public static void Apply(string setting)
    {
        var theme = setting switch
        {
            "Dark" => ApplicationTheme.Dark,
            "Light" => ApplicationTheme.Light,
            _ => FromSystem(),
        };

        if (theme == _applied)
        {
            return;
        }

        ApplicationThemeManager.Apply(theme);
        _applied = theme;

        // ThemeService is the only place the app theme changes, so the overrides are
        // re-synced here rather than through a theme-changed subscription.
        ApplyContrastOverrides(theme);

        // A real theme change goes through the same backdrop teardown as a no-op one, and the
        // panel is a standalone top-level window rather than an owned one, so put its backdrop
        // back explicitly instead of trusting the theme manager to reach it.
        RestoreBackdrops();
    }

    /// <summary>
    /// Re-applies each open window's declared backdrop. Best-effort: a window that is mid-close or
    /// on a machine where the effect is unsupported simply keeps whatever it has.
    /// </summary>
    private static void RestoreBackdrops(Window? except = null)
    {
        var windows = Application.Current?.Windows;
        if (windows is null)
        {
            return;
        }

        foreach (Window window in windows)
        {
            if (ReferenceEquals(window, except))
            {
                continue;
            }

            if (window is not FluentWindow fluent
                || fluent.WindowBackdropType == WindowBackdropType.None)
            {
                continue;
            }

            try
            {
                WindowBackdrop.ApplyBackdrop(window, fluent.WindowBackdropType);
            }
            catch (Exception ex)
            {
                // Losing the acrylic is a cosmetic regression; losing the theme switch is worse.
                AppLog.Error("Restore backdrop", ex);
            }
        }
    }

    /// <summary>
    /// WPF-UI tears down the backdrop of *every* window when a new FluentWindow is shown (its
    /// WindowBackgroundManager re-applies the background globally). Without this, opening any
    /// dialog left the settings window / panel painted flat white behind it. Schedule one restore
    /// per window after it finishes loading.
    /// </summary>
    public static void RestoreBackdropsWhenLoaded(Window window)
    {
        window.Loaded += (_, _) =>
        {
            // The teardown can land before or after the first Loaded dispatch depending on where
            // WPF-UI re-applies the background, so restore on a short schedule instead of once.
            RestoreLater(window, 0);
            RestoreLater(window, 120);
            RestoreLater(window, 350);
        };

        window.Closed += (_, _) => RestoreBackdrops();
    }

    private static void RestoreLater(Window window, int delayMs)
    {
        window.Dispatcher.InvokeAsync(async () =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(true);
            }

            if (window.IsLoaded)
            {
                RestoreBackdrops(window);
            }
        }, DispatcherPriority.Background);
    }

    private static void ApplyContrastOverrides(ApplicationTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // Whichever ramp is not active has to be dropped again, otherwise the previous theme's
        // brushes would keep shadowing the newly applied theme dictionary.
        var stale = theme == ApplicationTheme.Light ? DarkOverrides : LightOverrides;
        foreach (var (key, _) in stale)
        {
            resources.Remove(key);
        }

        var active = theme == ApplicationTheme.Light ? LightOverrides : DarkOverrides;
        foreach (var (key, color) in active)
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static ApplicationTheme FromSystem()
    {
        try
        {
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch
        {
            return ApplicationTheme.Dark;
        }
    }
}
