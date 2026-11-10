using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClipBar.App.Services;

public static class ThemeService
{
    /// <summary>
    /// Attached property declaring which WPF-UI backdrop a window wants (None for windows that
    /// paint their surface from AppSurfaceBrush). The windows are plain Windows, not WPF-UI
    /// FluentWindows: the FluentWindow template comes from the Application theme dictionary, and
    /// when a window is created during a dictionary swap the style resolved to nothing — no
    /// WindowChrome, and DWM painted a native system-themed caption over the content. Plain
    /// Window + explicit backing calls remove that failure class.
    /// </summary>
    public static readonly DependencyProperty DeclaredBackdropProperty =
        DependencyProperty.RegisterAttached(
            "DeclaredBackdrop",
            typeof(WindowBackdropType),
            typeof(ThemeService),
            new PropertyMetadata(WindowBackdropType.None));

    public static WindowBackdropType GetDeclaredBackdrop(Window window)
        => (WindowBackdropType)window.GetValue(DeclaredBackdropProperty);

    public static void SetDeclaredBackdrop(Window window, WindowBackdropType value)
        => window.SetValue(DeclaredBackdropProperty, value);

    /// <summary>
    /// Light palette and readable Fluent replacements. These values are pushed into
    /// Application.Resources, which shadows the merged theme dictionary, so every
    /// DynamicResource consumer (panel, tray menu, settings, dialogs) picks them up.
    /// </summary>
    private static readonly (string Key, Color Color)[] LightOverrides =
    [
        // Website-aligned, cool surfaces with quiet blue-gray borders.
        ("ApplicationBackgroundBrush", Color.FromRgb(0xF3, 0xF7, 0xFC)),
        ("ControlFillColorDefaultBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("ControlFillColorSecondaryBrush", Color.FromRgb(0xF7, 0xFA, 0xFE)),
        ("ControlFillColorTertiaryBrush", Color.FromRgb(0xEE, 0xF4, 0xFB)),
        ("ControlStrokeColorDefaultBrush", Color.FromRgb(0xD2, 0xDE, 0xEC)),
        ("ControlStrokeColorSecondaryBrush", Color.FromRgb(0xC2, 0xD2, 0xE4)),
        ("ControlStrokeColorTertiaryBrush", Color.FromRgb(0xE5, 0xED, 0xF5)),
        ("SurfaceStrokeColorDefaultBrush", Color.FromRgb(0xDC, 0xE6, 0xF2)),
        ("DividerStrokeColorDefaultBrush", Color.FromRgb(0xE1, 0xE9, 0xF2)),
        ("ControlStrongFillColorDefaultBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("SubtleFillColorSecondaryBrush", Color.FromRgb(0xEC, 0xF4, 0xFE)),
        ("SubtleFillColorTertiaryBrush", Color.FromRgb(0xE0, 0xEE, 0xFF)),

        // Deep navy carries small text; the vivid blue is reserved for accents and artwork.
        ("TextFillColorPrimaryBrush", Color.FromRgb(0x14, 0x26, 0x3D)),
        ("TextFillColorSecondaryBrush", Color.FromRgb(0x2D, 0x44, 0x5D)),
        ("TextFillColorTertiaryBrush", Color.FromRgb(0x3E, 0x56, 0x70)),
        ("TextPlaceholderColorBrush", Color.FromRgb(0x42, 0x5A, 0x74)),
        ("TextFillColorDisabledBrush", Color.FromRgb(0x6C, 0x7E, 0x92)),

        // Filled controls use the darker brand blue so white labels remain legible.
        ("SystemAccentColorBrush", Color.FromRgb(0x0A, 0x83, 0xF2)),
        ("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("AccentFillColorSecondaryBrush", Color.FromRgb(0x00, 0x52, 0xA2)),
        ("AccentFillColorTertiaryBrush", Color.FromRgb(0x00, 0x48, 0x8D)),
        ("AccentTextFillColorPrimaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentTextFillColorSecondaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentTextFillColorTertiaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        // WPF-UI buttons resolve their own keys, not the generic accent fill brushes.
        ("AccentButtonBackground", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("AccentButtonBackgroundPointerOver", Color.FromRgb(0x00, 0x52, 0xA2)),
        ("AccentButtonBackgroundPressed", Color.FromRgb(0x00, 0x48, 0x8D)),
        ("AccentButtonForeground", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentButtonForegroundPointerOver", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentButtonForegroundPressed", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("LogoBackgroundBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("LogoForegroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),

        // Semantic brushes shared by the custom history card templates.
        ("BrandBrush", Color.FromRgb(0x0A, 0x83, 0xF2)),
        ("BrandStrongBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("HistoryCardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("HistoryCardBorderBrush", Color.FromRgb(0xE1, 0xEA, 0xF4)),
        ("HistoryHoverBrush", Color.FromRgb(0xEF, 0xF6, 0xFF)),
        ("HistorySelectedBrush", Color.FromRgb(0xE4, 0xF1, 0xFF)),
        ("HistorySelectedBorderBrush", Color.FromRgb(0x83, 0xBD, 0xF3)),
        ("HistoryFocusBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("TextBadgeBrush", Color.FromRgb(0xE8, 0xF2, 0xFF)),
        ("ImageBadgeBrush", Color.FromRgb(0xE3, 0xF5, 0xF1)),
        ("FileBadgeBrush", Color.FromRgb(0xFF, 0xF3, 0xD8)),
    ];

    /// <summary>Dark counterparts to the shared semantic palette, with readable pale text.</summary>
    private static readonly (string Key, Color Color)[] DarkOverrides =
    [
        ("ApplicationBackgroundBrush", Color.FromRgb(0x10, 0x24, 0x3D)),
        ("ControlFillColorDefaultBrush", Color.FromRgb(0x1B, 0x33, 0x4E)),
        ("ControlFillColorSecondaryBrush", Color.FromRgb(0x21, 0x3D, 0x5B)),
        ("ControlFillColorTertiaryBrush", Color.FromRgb(0x28, 0x47, 0x65)),
        ("ControlStrokeColorDefaultBrush", Color.FromRgb(0x38, 0x52, 0x6D)),
        ("ControlStrokeColorSecondaryBrush", Color.FromRgb(0x4B, 0x64, 0x7E)),
        ("ControlStrokeColorTertiaryBrush", Color.FromRgb(0x2B, 0x42, 0x5B)),
        ("SurfaceStrokeColorDefaultBrush", Color.FromRgb(0x30, 0x4B, 0x67)),
        ("DividerStrokeColorDefaultBrush", Color.FromRgb(0x29, 0x42, 0x5C)),
        ("ControlStrongFillColorDefaultBrush", Color.FromRgb(0x7D, 0xBB, 0xFF)),
        ("SubtleFillColorSecondaryBrush", Color.FromRgb(0x1C, 0x3B, 0x5A)),
        ("SubtleFillColorTertiaryBrush", Color.FromRgb(0x24, 0x48, 0x6A)),

        ("TextFillColorPrimaryBrush", Color.FromRgb(0xF5, 0xF8, 0xFC)),
        ("TextFillColorSecondaryBrush", Color.FromRgb(0xDC, 0xE8, 0xF6)),
        ("TextFillColorTertiaryBrush", Color.FromRgb(0xC1, 0xD2, 0xE5)),
        ("TextPlaceholderColorBrush", Color.FromRgb(0xBD, 0xCE, 0xE2)),
        ("TextFillColorDisabledBrush", Color.FromRgb(0x89, 0x9B, 0xB0)),

        ("SystemAccentColorBrush", Color.FromRgb(0x70, 0xB9, 0xFF)),
        ("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("AccentFillColorSecondaryBrush", Color.FromRgb(0x00, 0x52, 0xA2)),
        ("AccentFillColorTertiaryBrush", Color.FromRgb(0x00, 0x48, 0x8D)),
        ("AccentTextFillColorPrimaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentTextFillColorSecondaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentTextFillColorTertiaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        // WPF-UI buttons resolve their own keys, not the generic accent fill brushes.
        ("AccentButtonBackground", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("AccentButtonBackgroundPointerOver", Color.FromRgb(0x00, 0x52, 0xA2)),
        ("AccentButtonBackgroundPressed", Color.FromRgb(0x00, 0x48, 0x8D)),
        ("AccentButtonForeground", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentButtonForegroundPointerOver", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("AccentButtonForegroundPressed", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("LogoBackgroundBrush", Color.FromRgb(0x00, 0x5F, 0xB9)),
        ("LogoForegroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)),

        ("BrandBrush", Color.FromRgb(0x70, 0xB9, 0xFF)),
        ("BrandStrongBrush", Color.FromRgb(0x80, 0xBD, 0xFF)),
        ("HistoryCardBrush", Color.FromRgb(0x1B, 0x33, 0x4E)),
        ("HistoryCardBorderBrush", Color.FromRgb(0x2B, 0x45, 0x60)),
        ("HistoryHoverBrush", Color.FromRgb(0x20, 0x3F, 0x60)),
        ("HistorySelectedBrush", Color.FromRgb(0x1F, 0x45, 0x69)),
        ("HistorySelectedBorderBrush", Color.FromRgb(0x66, 0xAD, 0xF2)),
        ("HistoryFocusBrush", Color.FromRgb(0x80, 0xBD, 0xFF)),
        ("TextBadgeBrush", Color.FromRgb(0x1E, 0x3D, 0x5B)),
        ("ImageBadgeBrush", Color.FromRgb(0x1D, 0x41, 0x45)),
        ("FileBadgeBrush", Color.FromRgb(0x4B, 0x40, 0x29)),
    ];

    /// <summary>
    /// The theme currently pushed into the resource dictionaries, or <c>null</c> before the first
    /// apply. Kept so a repeated apply of the same theme can be skipped.
    /// </summary>
    private static ApplicationTheme? _applied;

    /// <summary>The theme currently applied to the dictionaries (null before the first Apply).</summary>
    public static ApplicationTheme? AppliedTheme => _applied;

    /// <summary>The saved preference ("System"/"Light"/"Dark"), used when High Contrast toggles.</summary>
    private static string _currentSetting = "System";

    static ThemeService()
    {
        ApplicationThemeManager.Changed += OnThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private static void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast))
        {
            return;
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(ReapplyCurrentSetting));
            return;
        }

        ReapplyCurrentSetting();
    }

    /// <summary>
    /// Windows raised a High Contrast change. Re-evaluate the saved preference so the
    /// high-contrast dictionary replaces the normal one (and is replaced again when the system
    /// setting turns off). Apply() is idempotent when the effective theme did not change.
    /// </summary>
    private static void ReapplyCurrentSetting() => Apply(_currentSetting);

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
        _currentSetting = setting;
        var theme = Platform.ThemeSelection.Resolve(
            setting, SystemParameters.HighContrast, FromSystem());

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

        // The pinned DWM border colour is theme-aware (dark hairline on the dark surface, light
        // hairline on the light one); a theme switch must repaint every open window's frame.
        var openWindows = Application.Current?.Windows;
        var windowList = new List<Window>();
        if (openWindows is not null)
        {
            foreach (Window ow in openWindows)
            {
                windowList.Add(ow);
            }
        }

        foreach (var window in windowList)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Platform.FrameBorder.Repin(hwnd);
                }
            }
            catch
            {
                // A window mid-close has no hwnd; the next activation re-pins anyway.
            }
        }

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

            if (window is not Window w)
            {
                continue;
            }

            var declared = GetDeclaredBackdrop(w);
            if (declared == WindowBackdropType.None)
            {
                continue;
            }

            try
            {
                WindowBackdrop.ApplyBackdrop(w, declared);
            }
            catch (Exception ex)
            {
                // Losing the acrylic is a cosmetic regression; losing the theme switch is worse.
                AppLog.Error("Restore backdrop", ex);
            }
        }
    }

    private static void ApplyContrastOverrides(ApplicationTheme theme)
    {
        if (Application.Current?.Resources is { } resources)
        {
            ApplyPalette(resources, theme);
        }
    }

    internal static void ApplyPalette(ResourceDictionary resources, ApplicationTheme theme)
    {
        if (!Platform.ThemeSelection.ShouldApplyCustomPalette(theme))
        {
            // High Contrast owns the palette. Drop every custom palette entry so it cannot
            // shadow the system dictionary, then use system brushes for custom history states.
            foreach (var (key, _) in LightOverrides)
            {
                resources.Remove(key);
            }

            foreach (var (key, _) in DarkOverrides)
            {
                resources.Remove(key);
            }

            resources["SurfaceLayerOpacity"] = 1.0d;
            resources["BrandBrush"] = SystemColors.HighlightBrush;
            resources["BrandStrongBrush"] = SystemColors.HighlightBrush;
            resources["LogoBackgroundBrush"] = SystemColors.HighlightBrush;
            resources["LogoForegroundBrush"] = SystemColors.HighlightTextBrush;
            resources["HistoryCardBrush"] = SystemColors.WindowBrush;
            resources["HistoryCardBorderBrush"] = SystemColors.WindowBrush;
            resources["HistoryHoverBrush"] = SystemColors.ControlBrush;
            resources["HistorySelectedBrush"] = SystemColors.WindowBrush;
            resources["HistorySelectedBorderBrush"] = SystemColors.HighlightBrush;
            resources["HistoryFocusBrush"] = SystemColors.HighlightBrush;
            resources["TextBadgeBrush"] = SystemColors.ControlBrush;
            resources["ImageBadgeBrush"] = SystemColors.ControlBrush;
            resources["FileBadgeBrush"] = SystemColors.ControlBrush;
            return;
        }

        // The self-drawn surface layer (panel, menus, dialogs) sits over the DWM backdrop.
        // The system backdrop tint follows the WINDOWS theme, not the app theme: a light app
        // theme on a dark Windows mode mixed to a muddy gray (the user's 浅色模式灰不拉几).
        // A higher layer opacity in light mode lets the theme surface dominate the blend.
        resources["SurfaceLayerOpacity"] = theme == ApplicationTheme.Light ? 0.96d : 0.88d;

        // Drop the previous theme palette so none of its brushes shadow the newly applied one.
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

        var color = _applied == ApplicationTheme.HighContrast
            ? SystemColors.WindowColor
            : _applied == ApplicationTheme.Dark
                ? Color.FromRgb(0x10, 0x24, 0x3D)
                : Color.FromRgb(0xF3, 0xF7, 0xFC);

        if (_applied != ApplicationTheme.HighContrast
            && app.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush fluent)
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

        // The pinned DWM border colour is theme-aware (dark hairline on the dark surface, light
        // hairline on the light one); a theme switch must repaint every open window's frame.
        var openWindows = Application.Current?.Windows;
        var windowList = new List<Window>();
        if (openWindows is not null)
        {
            foreach (Window ow in openWindows)
            {
                windowList.Add(ow);
            }
        }

        foreach (var window in windowList)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Platform.FrameBorder.Repin(hwnd);
                }
            }
            catch
            {
                // A window mid-close has no hwnd; the next activation re-pins anyway.
            }
        }

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
            if (window.AllowsTransparency || GetDeclaredBackdrop(window) != WindowBackdropType.None)
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
