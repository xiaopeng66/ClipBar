namespace ClipBar.App.Services;

/// <summary>Generation and focus checks for the clipboard-to-Ctrl+V handoff.</summary>
internal static class PasteClipboardPolicy
{
    public static bool MayRestore(uint pasteSequence, uint currentSequence)
        => currentSequence == pasteSequence;

    public static bool MaySendPaste(
        IntPtr targetWindow,
        IntPtr foregroundWindow,
        uint writtenSequence,
        uint currentSequence)
        => targetWindow != IntPtr.Zero
            && targetWindow == foregroundWindow
            && currentSequence == writtenSequence;
}
