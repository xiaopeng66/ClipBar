namespace ClipBar.Core.Models;

/// <summary>A SnipDo extension: one .pbar package unpacked into a folder.</summary>
public sealed class SnipDoExtension
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Unique id authored by the extension, e.g. "myextension.myname".</summary>
    public string Identifier { get; init; } = string.Empty;

    public string? Icon { get; init; }

    /// <summary>Colored variant of the icon; SnipDo shows it when monochrome is false.</summary>
    public string? IconColored { get; init; }

    /// <summary>True when the package only ships a single-color glyph.</summary>
    public bool Monochrome { get; init; }

    public string? RequiredAppVersion { get; init; }

    public string? RequiredOSVersion { get; init; }

    public Dictionary<string, string> ExtensionTranslations { get; init; } = [];

    public List<SnipDoAction> Actions { get; init; } = [];

    /// <summary>Folder the package was unpacked into.</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Absolute path of the extension icon, when the package ships one.</summary>
    public string? IconPath { get; init; }

    /// <summary>Display name, localized when the package provides translations.</summary>
    public string DisplayName => ExtensionTranslations.Count > 0
        ? SnipDoLocalization.Pick(ExtensionTranslations, Name)
        : Name;
}

/// <summary>One button of a SnipDo extension.</summary>
public sealed class SnipDoAction
{
    public string Title { get; init; } = string.Empty;

    public string? Icon { get; init; }

    /// <summary>Colored variant of the icon, preferred over <see cref="Icon"/> when present.</summary>
    public string? IconColored { get; init; }

    public bool Monochrome { get; init; }

    public Dictionary<string, string> ActionTranslations { get; init; } = [];

    /// <summary>Opens the resolved URL in the default browser.</summary>
    public string? Url { get; init; }

    /// <summary>Runs a PowerShell script shipped inside the package.</summary>
    public string? PowershellFile { get; init; }

    /// <summary>Best-effort support for IronPython-era packages.</summary>
    public string? PythonFile { get; init; }

    /// <summary>SendKeys combination sent to the previously active window.</summary>
    public string? KeyCombo { get; init; }

    /// <summary>PASTE_RESULT, SHOW_RESULT, SHOW_SUCCESS, or COPY_RESULT.</summary>
    public string? After { get; init; }

    public bool LongRunning { get; init; }

    public string? Regex { get; init; }

    public List<string> Requirements { get; init; } = [];

    public List<string> NoShowApps { get; init; } = [];

    public List<string> ShowOnlyApps { get; init; } = [];

    public List<SnipDoAppOption> Options { get; init; } = [];

    /// <summary>Folder the owning package was unpacked into, used to resolve scripts.</summary>
    public string ScriptRoot { get; init; } = string.Empty;

    public string DisplayTitle => ActionTranslations.Count > 0
        ? SnipDoLocalization.Pick(ActionTranslations, Title)
        : Title;

    public SnipDoActionKind Kind =>
        !string.IsNullOrWhiteSpace(Url) ? SnipDoActionKind.Url
        : !string.IsNullOrWhiteSpace(PowershellFile) ? SnipDoActionKind.PowerShell
        : !string.IsNullOrWhiteSpace(PythonFile) ? SnipDoActionKind.Python
        : !string.IsNullOrWhiteSpace(KeyCombo) ? SnipDoActionKind.KeyCombo
        : SnipDoActionKind.None;
}

public enum SnipDoActionKind
{
    None,
    Url,
    PowerShell,
    Python,
    KeyCombo,
}

/// <summary>A user-selectable value an action needs before it can run.</summary>
public sealed class SnipDoAppOption
{
    public string Identifier { get; init; } = string.Empty;

    /// <summary>Key shown to the user, value handed to the action.</summary>
    public Dictionary<string, string> Values { get; init; } = [];

    /// <summary>Script that produces the value on demand.</summary>
    public string? PowershellFile { get; init; }
}

/// <summary>Picks a translation with the same fallback rules SnipDo uses.</summary>
public static class SnipDoLocalization
{
    private static readonly string[] Preferred =
    [
        "zh-Hans", "zh-CN", "zh-Hans-CN", "zh", "en", "en-US",
    ];

    public static string Pick(Dictionary<string, string> translations, string fallback)
    {
        foreach (var key in Preferred)
        {
            var match = translations.FirstOrDefault(
                pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null && !string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback;
    }
}
