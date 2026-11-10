using System.Windows;
using System.Windows.Media;

namespace ClipBar.App.Services;

/// <summary>
/// Theme brush lookups that cannot fail. Fluent resource keys are read straight out of
/// <c>Application.Current.Resources</c> in a few code-behind files, and an indexer miss returns
/// null while a wrong type throws <c>InvalidCastException</c> — either way the dialog that
/// was being built loses its text colour or dies. These helpers fall back to a readable colour
/// and keep the window usable.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>Secondary body text, used for hints and labels.</summary>
    public static Brush SecondaryText { get; } = Create(0x5C, 0x5C, 0x5C);

    /// <summary>Primary body text.</summary>
    public static Brush PrimaryText { get; } = Create(0x1A, 0x1A, 0x1A);

    /// <summary>Hairline divider.</summary>
    public static Brush Divider { get; } = Create(0x33, 0x33, 0x33);

    /// <summary>Error / destructive accent.</summary>
    public static Brush Critical { get; } = Create(0xC4, 0x2B, 0x1C);

    /// <summary>
    /// Resolves a theme brush by key, preferring the live application resource so the colour
    /// follows the active theme, and falling back to <paramref name="fallback"/> when the key
    /// is absent or holds something that is not a brush.
    /// </summary>
    public static Brush Resolve(string key, Brush fallback)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            try
            {
                if (resources[key] is Brush brush)
                {
                    return brush;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Resources.MissingManifestResourceException)
            {
                // A malformed or half-initialised dictionary must not stop a dialog from opening.
            }
        }

        return fallback;
    }

    private static Brush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
