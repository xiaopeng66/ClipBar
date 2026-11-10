using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipBar.App.Services;

/// <summary>
/// Marks the search term inside a title (A7). Two attached properties instead of a binding on
/// TextBlock.Text: the highlighter owns the Inlines, so recycling a row re-splits the new title
/// with the current term. The split logic is pure and unit-tested.
/// </summary>
public static class TextHighlighter
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(TextHighlighter),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermProperty = DependencyProperty.RegisterAttached(
        "Term",
        typeof(string),
        typeof(TextHighlighter),
        new PropertyMetadata(null, OnChanged));

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetTerm(DependencyObject element, string? value) => element.SetValue(TermProperty, value);

    public static string? GetTerm(DependencyObject element) => (string?)element.GetValue(TermProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block)
        {
            Apply(block, GetSource(block), GetTerm(block));
        }
    }

    /// <summary>Splits <paramref name="text"/> into alternating non-match/match segments.</summary>
    public static IReadOnlyList<(string Text, bool Match)> Split(string? text, string? term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var needle = term?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return [(text, false)];
        }

        var result = new List<(string, bool)>();
        var index = 0;
        while (index < text.Length)
        {
            var hit = text.IndexOf(needle, index, StringComparison.CurrentCultureIgnoreCase);
            if (hit < 0)
            {
                result.Add((text[index..], false));
                break;
            }

            if (hit > index)
            {
                result.Add((text[index..hit], false));
            }

            result.Add((text.Substring(hit, needle.Length), true));
            index = hit + needle.Length;
        }

        return result;
    }

    private static void Apply(TextBlock block, string? source, string? term)
    {
        var segments = Split(source, term);
        block.Inlines.Clear();
        if (segments.Count == 0)
        {
            return;
        }

        var brush = HighlightBrush();
        foreach (var (text, match) in segments)
        {
            var run = new Run(text);
            if (match)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Background = brush;
            }

            block.Inlines.Add(run);
        }
    }

    // Per-accent cache: a frozen brush keyed by its colour, so a theme or accent change
    // produces a fresh brush instead of the first-run one living forever (R12-U-05).
    private static readonly Dictionary<Color, Brush> HighlightBrushCache = new();

    private static Brush HighlightBrush()
    {
        var accent = Application.Current?.TryFindResource("SystemAccentColor") as Color?;
        var color = accent ?? Color.FromRgb(0x00, 0x78, 0xD4);
        if (HighlightBrushCache.TryGetValue(color, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        brush.Freeze();
        HighlightBrushCache[color] = brush;
        return brush;
    }
}
