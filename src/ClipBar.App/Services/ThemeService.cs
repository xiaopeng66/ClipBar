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

    static ThemeService()
    {
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

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

        // Apply() raises Changed, which already re-synced the overrides and the surface brush;
        // repeat here so a theme manager that applies dictionaries without raising the event
        // still leaves the resources consistent.
        ApplyContrastOverrides(theme);
        SyncSurfaceBrush();
        RefreshWindowSurfaces();

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
        // Dialogs and menus now declare WindowBackdropType=None, so showing them no longer makes
        // WPF-UI tear down and rebuild every window's backdrop (the "panel turns white" bug) — and
        // we no longer repaint other windows while a dialog is opening (that was a flash source).
        // A window that used a real backdrop (the Mica settings window) still restores the others
        // when it closes.
        window.Closed += (_, _) => RestoreBackdrops();
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

    /// <summary>
    /// Keeps <c>AppSurfaceBrush</c> equal to the surface colour WPF-UI is actually painting, so
    /// our windows can never end up with white text over a light background (dark text over a
    /// dark one) after an external theme switch. Falls back to the nominal theme colours when
    /// the Fluent key is missing.
    /// </summary>
    private static void SyncSurfaceBrush()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var color = _applied == ApplicationTheme.Dark
            ? Color.FromRgb(0x20, 0x20, 0x20)
            : Color.FromRgb(0xF3, 0xF3, 0xF3);

        if (app.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush fluent)
        {
            // Match WPF-UI's own surface exactly when it is available.
            color = fluent.Color;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources["AppSurfaceBrush"] = brush;
    }

    /// <summary>
    /// WPF-UI can switch theme dictionaries itself (system theme changes with the "follow
    /// system" setting). Without this, our contrast overrides stayed on the previous theme and
    /// produced dark-theme white text on a light surface — the "dark theme is a white screen"
    /// report. The handler only rewrites resources; it never calls Apply again.
    /// </summary>
    private static void OnThemeChanged(ApplicationTheme theme, Color systemAccent)
    {
        _applied = theme;
        ApplyContrastOverrides(theme);
        SyncSurfaceBrush();
        RefreshWindowSurfaces();

        // Changed fires before WPF-UI rebuilds window backgrounds; restoring immediately would
        // be overwritten. One dispatcher turn later the theme manager is done.
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                RestoreBackdrops();
                RefreshWindowSurfaces();
            }),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Windows with <see cref="WindowBackdropType.None"/> paint their surface from
    /// <c>AppSurfaceBrush</c>. WPF-UI's theme manager can replace that Background with a local
    /// brush while switching themes, which silently detaches the DynamicResource — the window
    /// then keeps the previous theme's surface while text colours move on. Rebind explicitly.
    /// </summary>
    private static void RefreshWindowSurfaces()
    {
        var windows = Application.Current?.Windows;
        if (windows is null)
        {
            return;
        }

        foreach (Window window in windows)
        {
            if (window is FluentWindow fluent && fluent.WindowBackdropType != WindowBackdropType.None)
            {
                continue;
            }

            window.SetResourceReference(Window.BackgroundProperty, "AppSurfaceBrush");
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
