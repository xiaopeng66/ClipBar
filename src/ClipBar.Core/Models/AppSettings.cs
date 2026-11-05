namespace ClipBar.Core.Models;

public sealed class AppSettings
{
    /// <summary>Settings-file format version (C2). Old files without it deserialize to this.</summary>
    public int Version { get; set; } = 2;

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

    public int PanelLeft { get; set; }

    public int PanelTop { get; set; }

    public int PanelWidth { get; set; } = 420;

    public int PanelHeight { get; set; } = 620;

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
        PanelLeft = PanelLeft,
        PanelTop = PanelTop,
        PanelWidth = PanelWidth,
        PanelHeight = PanelHeight,
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
        PanelWidth = Math.Clamp(PanelWidth <= 0 ? 420 : PanelWidth, 360, 2000);
        PanelHeight = Math.Clamp(PanelHeight <= 0 ? 620 : PanelHeight, 480, 1600);
        Hotkey = Hotkey?.Trim() ?? string.Empty;
        Theme = CanonicalTheme(Theme);
        ImagePreview = CanonicalImagePreview(ImagePreview);
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
            && left.PanelLeft == right.PanelLeft
            && left.PanelTop == right.PanelTop
            && left.PanelWidth == right.PanelWidth
            && left.PanelHeight == right.PanelHeight
            && left.StartWithWindows == right.StartWithWindows
            && left.CaptureImages == right.CaptureImages
            && left.CaptureFiles == right.CaptureFiles
            && left.CaptureHtml == right.CaptureHtml
            && left.ExcludedApps.Count == right.ExcludedApps.Count
            && left.ExcludedApps
                .Zip(right.ExcludedApps)
                .All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }
}
