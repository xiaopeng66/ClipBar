namespace ClipBar.Core.Models;

public sealed class AppSettings
{
    /// <summary>Settings-file format version (C2). Old files without it deserialize to this.</summary>
    public int Version { get; set; } = 3;

    public string Hotkey { get; set; } = "Win+Alt+V";

    public int MaxItems { get; set; } = 500;

    /// <summary>System, Light, or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// How an image clip is previewed in the panel: "Large" scales the screenshot to the card's
    /// content width, "Medium" caps it to a fixed box, "Small" puts it in the 40 DIP icon slot on
    /// the left of a normal list row. Large and Medium hide the title/subtitle until the pointer
    /// is over the image; Small always shows them.
    /// </summary>
    public string ImagePreview { get; set; } = "Large";

    public bool PasteOnClick { get; set; } = true;

    /// <summary>Stay open after a paste so several clips can be pasted in a row (A3, v1.1.0).</summary>
    public bool KeepPanelOpenAfterPaste { get; set; }

    /// <summary>
    /// Panel geometry (A5). Stored in DIPs; <see cref="HasPanelPosition"/> false means "centre on
    /// the cursor's monitor" (the pre-v1.1.0 behaviour). A restored position is clamped to the
    /// monitor's work area at show time.
    /// </summary>
    public bool HasPanelPosition { get; set; }

    /// <summary>
    /// Where the panel opens: "Remember" restores <see cref="PanelLeft"/>/<see cref="PanelTop"/>,
    /// "MonitorCenter" centres it on the monitor under the pointer, "CursorAnchor" opens it next
    /// to the pointer itself (direction chosen by free space). Size is always remembered.
    /// </summary>
    public string PanelPositionMode { get; set; } = "MonitorCenter";

    /// <summary>
    /// True when <see cref="PanelLeft"/>/<see cref="PanelTop"/> are physical screen pixels (v1.1.1+).
    /// Older files stored DIPs; the restore path converts those with the window scale.
    /// </summary>
    public bool PanelPositionInPixels { get; set; }

    public int PanelLeft { get; set; }

    public int PanelTop { get; set; }

    public int PanelWidth { get; set; } = 420;

    public int PanelHeight { get; set; } = 620;

    /// <summary>
    /// Enables ClipBar's own floating clipboard bar (hold left button over a text field, or drag
    /// select text) without SnipDo. Off by default: it installs a global input listener.
    /// </summary>
    public bool NativeFloatingEnabled { get; set; }

    /// <summary>Show the floating bar after the left button is held still this long.</summary>
    public int HoldDurationMs { get; set; } = 600;

    /// <summary>Moving further than this many pixels turns a press into a drag-select.</summary>
    public int DragThresholdPx { get; set; } = 15;

    /// <summary>Hovering the bar's clipboard button this long expands the compact list.</summary>
    public int HoverExpandMs { get; set; } = 200;

    /// <summary>Close the floating bar (and its list) this long after the pointer leaves.</summary>
    public int FloatingAutoHideMs { get; set; } = 1000;

    /// <summary>Also show the bar when a drag selected text (not only on hold).</summary>
    public bool InvokeOnDrag { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool CaptureImages { get; set; } = true;

    public bool CaptureFiles { get; set; } = true;

    public bool CaptureHtml { get; set; } = true;

    public List<string> ExcludedApps { get; set; } =
    [
        "1Password.exe",
        "Bitwarden.exe",
        "KeePass.exe",
        "KeePassXC.exe",
        "Keeper.exe",
    ];

    public AppSettings Clone() => new()
    {
        Version = Version,
        Hotkey = Hotkey,
        MaxItems = MaxItems,
        Theme = Theme,
        ImagePreview = ImagePreview,
        PasteOnClick = PasteOnClick,
        KeepPanelOpenAfterPaste = KeepPanelOpenAfterPaste,
        HasPanelPosition = HasPanelPosition,
        PanelPositionMode = PanelPositionMode,
        PanelPositionInPixels = PanelPositionInPixels,
        PanelLeft = PanelLeft,
        PanelTop = PanelTop,
        PanelWidth = PanelWidth,
        PanelHeight = PanelHeight,
        NativeFloatingEnabled = NativeFloatingEnabled,
        HoldDurationMs = HoldDurationMs,
        DragThresholdPx = DragThresholdPx,
        HoverExpandMs = HoverExpandMs,
        FloatingAutoHideMs = FloatingAutoHideMs,
        InvokeOnDrag = InvokeOnDrag,
        StartWithWindows = StartWithWindows,
        CaptureImages = CaptureImages,
        CaptureFiles = CaptureFiles,
        CaptureHtml = CaptureHtml,
        ExcludedApps = [.. (ExcludedApps ?? [])],
    };

    /// <summary>
    /// Canonicalises the values a user can type loosely: clamps the item count to the supported
    /// range, trims the hotkey and the excluded-app names, drops blank entries and removes
    /// case-insensitive duplicates. Applied both when saving and when comparing, so a form that
    /// differs from the live settings only by stray whitespace is not treated as an edit.
    /// Every field tolerates null: a hand-edited settings.json can carry explicit nulls, and an
    /// NRE here used to bypass the settings-store quarantine and crash every startup (G-03).
    /// </summary>
    public AppSettings Normalize()
    {
        MaxItems = Math.Clamp(MaxItems, 10, 5000);
        HoldDurationMs = Math.Clamp(HoldDurationMs <= 0 ? 600 : HoldDurationMs, 200, 2000);
        DragThresholdPx = Math.Clamp(DragThresholdPx <= 0 ? 15 : DragThresholdPx, 4, 100);
        HoverExpandMs = Math.Clamp(HoverExpandMs < 0 ? 200 : HoverExpandMs, 0, 2000);
        FloatingAutoHideMs = Math.Clamp(FloatingAutoHideMs <= 0 ? 1000 : FloatingAutoHideMs, 300, 10000);
        PanelWidth = Math.Clamp(PanelWidth <= 0 ? 420 : PanelWidth, 360, 2000);
        PanelHeight = Math.Clamp(PanelHeight <= 0 ? 620 : PanelHeight, 480, 1600);
        Hotkey = Hotkey?.Trim() ?? string.Empty;
        Theme = CanonicalTheme(Theme);
        ImagePreview = CanonicalImagePreview(ImagePreview);
        PanelPositionMode = CanonicalPanelPositionMode(PanelPositionMode);
        ExcludedApps ??= [];
        ExcludedApps = ExcludedApps
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return this;
    }

    /// <summary>Maps any stored or typed preview value onto the three supported sizes. Legacy
    /// "Original"/"Thumbnail" map onto Large/Medium so an upgrade keeps the user's pick (G-24:
    /// one canonical mapping shared by settings, the window and the rows).</summary>
    /// <summary>
    /// Three real modes: Remember restores the saved spot, MonitorCenter centres on the monitor
    /// under the pointer, CursorAnchor opens next to the pointer itself. Legacy aliases keep
    /// their old behaviour: "memory"/"fixed" were Remember, and FollowCursor (the pre-1.2 name)
    /// was always monitor-centre — an upgrade must not silently change it to pointer-anchoring.
    /// </summary>
    public static string CanonicalPanelPositionMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "remember" or "memory" or "fixed" => "Remember",
        "cursoranchor" or "nearcursor" or "cursor" => "CursorAnchor",
        _ => "MonitorCenter",
    };

    /// <summary>Only System/Light/Dark are real themes; anything else falls back to System (C2).</summary>
    public static string CanonicalTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System",
    };

    public static string CanonicalImagePreview(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "small" => "Small",
        "medium" or "thumbnail" => "Medium",
        "large" or "original" => "Large",
        _ => "Large",
    };

    /// <summary>
    /// Field-by-field equality over the normalised values. There is deliberately no
    /// <c>Equals</c> override on the type (it is a mutable settings bag), so callers that need
    /// "is the form dirty?" go through this instead of reference equality.
    /// </summary>
    public bool ContentEquals(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var left = Clone().Normalize();
        var right = other.Clone().Normalize();

        return string.Equals(left.Hotkey, right.Hotkey, StringComparison.OrdinalIgnoreCase)
            && left.MaxItems == right.MaxItems
            && string.Equals(left.Theme, right.Theme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ImagePreview, right.ImagePreview, StringComparison.OrdinalIgnoreCase)
            && left.PasteOnClick == right.PasteOnClick
            && left.KeepPanelOpenAfterPaste == right.KeepPanelOpenAfterPaste
            && left.HasPanelPosition == right.HasPanelPosition
            && string.Equals(left.PanelPositionMode, right.PanelPositionMode, StringComparison.OrdinalIgnoreCase)
            && left.PanelPositionInPixels == right.PanelPositionInPixels
            && left.PanelLeft == right.PanelLeft
            && left.PanelTop == right.PanelTop
            && left.PanelWidth == right.PanelWidth
            && left.PanelHeight == right.PanelHeight
            && left.StartWithWindows == right.StartWithWindows
            && left.CaptureImages == right.CaptureImages
            && left.CaptureFiles == right.CaptureFiles
            && left.CaptureHtml == right.CaptureHtml
            && left.NativeFloatingEnabled == right.NativeFloatingEnabled
            && left.InvokeOnDrag == right.InvokeOnDrag
            && left.HoldDurationMs == right.HoldDurationMs
            && left.DragThresholdPx == right.DragThresholdPx
            && left.HoverExpandMs == right.HoverExpandMs
            && left.FloatingAutoHideMs == right.FloatingAutoHideMs
            && left.ExcludedApps.Count == right.ExcludedApps.Count
            && left.ExcludedApps
                .Zip(right.ExcludedApps)
                .All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }
}
