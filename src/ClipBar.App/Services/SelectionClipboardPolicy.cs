namespace ClipBar.App.Services;

/// <summary>Generation checks for the clipboard-writing Ctrl+C selection fallback.</summary>
internal static class SelectionClipboardPolicy
{
    public static bool HasChanged(uint originalSequence, uint currentSequence)
        => currentSequence != originalSequence;

    public static bool HasSelectionText(string? text)
        => !string.IsNullOrEmpty(text);

    public static bool MayRestore(uint probeSequence, uint currentSequence)
        => currentSequence == probeSequence;
}
