namespace ClipBar.Core.Models;

public sealed class AppSettings
{
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
        Hotkey = Hotkey,
        MaxItems = MaxItems,
        Theme = Theme,
        ImagePreview = ImagePreview,
        PasteOnClick = PasteOnClick,
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
        Hotkey = Hotkey?.Trim() ?? string.Empty;
        Theme = Theme?.Trim() ?? string.Empty;
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
