using ClipBar.Core.Models;

namespace ClipBar.App.Services;

/// <summary>Which built-in action a clip menu row stands for.</summary>
public enum ClipMenuActionKind
{
    Separator,
    PasteAsPlainText,
    SaveImageAs,
    SearchWithBing,
}

/// <summary>One row of the clip context menu, before it is turned into a menu entry.</summary>
public readonly record struct ClipMenuEntry(string Title, ClipMenuActionKind Kind);

/// <summary>
/// Builds the rows of a clip's context menu. Pure: the window turns the result into
/// <c>TrayMenuEntry</c> actions, so what the menu offers can be tested without a desktop.
/// <para>
/// The menu is entirely first-party: plain-text paste, image save-as, and the optional Bing
/// search. Nothing external can add a row.
/// </para>
/// </summary>
public static class ClipMenuBuilder
{
    public static IReadOnlyList<ClipMenuEntry> Build(ClipItem model, AppSettings settings)
    {
        var entries = new List<ClipMenuEntry>();

        switch (model.Kind)
        {
            case ClipKind.Text:
                entries.Add(new ClipMenuEntry("粘贴为纯文本", ClipMenuActionKind.PasteAsPlainText));
                if (settings.ShowBingSearch && !string.IsNullOrWhiteSpace(model.Text))
                {
                    entries.Add(new ClipMenuEntry("用 Bing 搜索", ClipMenuActionKind.SearchWithBing));
                }

                break;
            case ClipKind.Image:
                entries.Add(new ClipMenuEntry("图片另存为…", ClipMenuActionKind.SaveImageAs));
                break;
        }

        return entries;
    }
}
