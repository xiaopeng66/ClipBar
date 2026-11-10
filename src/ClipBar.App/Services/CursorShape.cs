using ClipBar.App.Platform;

namespace ClipBar.App.Services;

/// <summary>What the pointer's shape says about the surface underneath it.</summary>
public enum CursorShapeKind
{
    /// <summary>The system I-beam: the window itself says "text here".</summary>
    IBeam,

    /// <summary>A known non-text cursor (arrow, hand, resize, cross…): definitely no text.</summary>
    KnownNonText,

    /// <summary>Some other handle: an application's custom cursor. Undecided.</summary>
    Unknown,
}

/// <summary>
/// Classifies the pointer's current shape.
/// <para>
/// This is the right first filter: the shape is what the window itself decided to show, and
/// reading it is a single cheap Win32 call — no cross-process COM or waiting on another
/// application. Standard I-beam and non-text cursors are classified directly; UI Automation
/// handles unknown cursors and can distinguish editable surfaces from read-only ones.
/// </para>
/// </summary>
public static class CursorShape
{
    /// <summary>The shared handle of the standard I-beam.</summary>
    public static readonly IntPtr IBeamHandle =
        NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(NativeMethods.IdiIBeam));

    /// <summary>Standard cursors that are never shown over text.</summary>
    public static readonly IReadOnlySet<IntPtr> NonTextHandles = LoadNonTextHandles();

    private static HashSet<IntPtr> LoadNonTextHandles()
    {
        int[] ids =
        [
            NativeMethods.IdiArrow,
            NativeMethods.IdiWait,
            NativeMethods.IdiCross,
            NativeMethods.IdiUpArrow,
            NativeMethods.IdiSizeNwse,
            NativeMethods.IdiSizeNesw,
            NativeMethods.IdiSizeWe,
            NativeMethods.IdiSizeNs,
            NativeMethods.IdiSizeAll,
            NativeMethods.IdiNo,
            NativeMethods.IdiHand,
            NativeMethods.IdiAppStarting,
            NativeMethods.IdiHelp,
        ];

        var set = new HashSet<IntPtr>();
        foreach (var id in ids)
        {
            var handle = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(id));
            if (handle != IntPtr.Zero)
            {
                set.Add(handle);
            }
        }

        return set;
    }

    /// <summary>Classifies one cursor handle. Pure, so the rule can be tested without a desktop.</summary>
    public static CursorShapeKind Classify(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero)
        {
            return CursorShapeKind.KnownNonText;
        }

        if (IBeamHandle != IntPtr.Zero && cursor == IBeamHandle)
        {
            return CursorShapeKind.IBeam;
        }

        return NonTextHandles.Contains(cursor) ? CursorShapeKind.KnownNonText : CursorShapeKind.Unknown;
    }

    /// <summary>
    /// Classifies the cursor currently drawn for the screen. Returns
    /// <see cref="CursorShapeKind.KnownNonText"/> when the cursor is hidden or the query fails,
    /// so a failed read never produces a popup.
    /// </summary>
    public static CursorShapeKind Read()
    {
        var info = default(NativeMethods.CursorInfo);
        info.Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CursorInfo>();

        try
        {
            if (!NativeMethods.GetCursorInfo(ref info) || info.Flags != NativeMethods.CursorShowing)
            {
                return CursorShapeKind.KnownNonText;
            }
        }
        catch (Exception)
        {
            return CursorShapeKind.KnownNonText;
        }

        return Classify(info.Cursor);
    }
}
