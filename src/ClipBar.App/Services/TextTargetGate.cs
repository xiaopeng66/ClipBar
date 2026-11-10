using System.Windows.Automation;
using ClipBar.App.Platform;

namespace ClipBar.App.Services;

/// <summary>
/// The facts about one UI Automation element that decide whether it is a text input.
/// Split out from <see cref="AutomationElement"/> so the rule itself can be unit tested without
/// a desktop or a real provider.
/// </summary>
public readonly record struct TextTargetFacts(
    string? ControlType,
    bool SupportsValue,
    bool ValueReadOnly,
    bool SupportsText,
    bool TextReadOnly)
{
    /// <summary>No element at all (nothing under the pointer, or the provider failed).</summary>
    public static TextTargetFacts None => new(null, false, false, false, false);
}

/// <summary>
/// Decides whether the pointer is over a place where the user can actually type.
/// <para>
/// Two signals, in this order:
/// </para>
/// <list type="number">
/// <item>
/// <b>The pointer's shape.</b> This is the right first filter:
/// the window decides which cursor to show, and reading it is one cheap Win32 call. A standard
/// non-text cursor (arrow, hand, resize…) is a definite no; the I-beam is a definite yes.
/// </item>
/// <item>
/// <b>UI Automation, only for unrecognised cursors.</b> A custom cursor handle tells us nothing,
/// so the element under the pointer is asked instead. This is also where a read-only surface
/// (browser body, PDF viewer) is separated from an editable one: those show an I-beam, so the
/// cursor alone would accept them.
/// </item>
/// </list>
/// <para>
/// The gesture used to be gated on UI Automation alone, which was slow (the first call to a
/// provider measured 614 ms) and missed custom-drawn editors that expose no usable control type.
/// </para>
/// </summary>
public sealed class TextTargetGate
{
    /// <summary>
    /// Hard ceiling for the UIA fallback. Generous on purpose: the first UIA call in a process to
    /// a given provider loads the client-side provider and measured 614 ms against WinForms, while
    /// warm calls take ~20 ms. Only unrecognised cursor shapes reach this path.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// True when <paramref name="facts"/> describe somewhere the user can type: an edit control,
    /// a document with editable text, or a value control that is not read-only.
    /// <para>
    /// <paramref name="cursorIsIBeam"/> is the pointer's own answer, consulted only when the
    /// element reports nothing usable. Electron composers (QQ, Slack, Discord) expose their
    /// message box as an opaque <c>Pane</c> — no Edit type, no ValuePattern, no TextPattern — so
    /// refusing those means the feature never works in those applications. Anything that reaches
    /// that branch has already been through the pattern checks above, so a read-only surface that
    /// does report text is still refused; only an element that tells us nothing is decided by the
    /// cursor, which is the only signal available in this case.
    /// </para>
    /// </summary>
    public static bool IsTextTarget(TextTargetFacts facts, bool cursorIsIBeam = false)
    {
        // An explicit read-only marker always wins: a read-only document (browser reading view,
        // a PDF viewer) cannot receive a paste, so offering it there is wrong even though it
        // reports a text pattern.
        if (facts.SupportsValue)
        {
            return !facts.ValueReadOnly
                && (facts.ControlType is "Edit" or "Document" or "Text");
        }

        if (facts.SupportsText)
        {
            return !facts.TextReadOnly
                && facts.ControlType is "Edit" or "Document";
        }

        // Editors that expose neither pattern (some custom-drawn controls) still identify as an
        // Edit control; that is the most reliable signal available.
        if (facts.ControlType == "Edit")
        {
            return true;
        }

        // An element that says nothing at all: the I-beam is the only witness, and only for the
        // container types a text surface is drawn into.
        return cursorIsIBeam
            && facts.ControlType is "Pane" or "Group" or "Custom" or "Document" or "Text";
    }

    /// <summary>
    /// True when the press should be allowed to start a gesture. The cursor shape answers first;
    /// only <see cref="CursorShapeKind.Unknown"/> escalates to UI Automation.
    /// </summary>
    public async Task<bool> IsTextTargetAsync(
        CursorShapeKind cursor,
        int screenX,
        int screenY,
        CancellationToken cancellationToken = default)
    {
        // A standard non-text cursor is a definite no, and it covers the case the user reported:
        // the desktop, buttons, list rows and the taskbar. No cross-process call is made at all.
        if (cursor == CursorShapeKind.KnownNonText)
        {
            return false;
        }

        // I-beam, or an unrecognised custom cursor. The I-beam still needs the check: browsers
        // and PDF viewers show it over read-only text, which cannot receive a paste.
        return await IsEditableAsync(screenX, screenY, cursor == CursorShapeKind.IBeam, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<bool> IsEditableAsync(
        int screenX, int screenY, bool cursorIsIBeam, CancellationToken cancellationToken)
        => BoundedUiAutomation.RunAsync(
            () => ReadAt(screenX, screenY, cursorIsIBeam),
            Timeout,
            fallback: false,
            cancellationToken);

    /// <summary>
    /// Initialises the UI Automation client on a worker thread. The first UIA call in a process
    /// pays a one-off cost (measured at ~600 ms) while the client loads; paying it during startup
    /// keeps the first press-and-hold from stalling on it.
    /// </summary>
    public static Task WarmUpAsync()
        => Task.Run(() =>
        {
            try
            {
                _ = AutomationElement.RootElement;
            }
            catch (Exception)
            {
                // Warm-up is best effort: a failure means the first real call pays the cost.
            }
        });

    private static bool ReadAt(int screenX, int screenY, bool cursorIsIBeam)
    {
        // WindowFromPoint is the cheap pre-filter: a window that ignores mouse input (an overlay,
        // a cloak) has no editable surface under it.
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.Point(screenX, screenY));
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        AutomationElement? fromPoint;
        try
        {
            fromPoint = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Text target gate: FromPoint failed: {ex.GetType().Name}: {ex.Message}");
            fromPoint = null;
        }

        if (fromPoint is not null && Judge(fromPoint, cursorIsIBeam))
        {
            return true;
        }

        // FromPoint is not always the deepest element: WinForms answers with the top-level Window
        // for a synthesised hover, while WindowFromPoint has already resolved the real EDIT
        // control's HWND. Ask that HWND's own element before refusing.
        AutomationElement? fromHandle = null;
        try
        {
            fromHandle = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception)
        {
            // A window without a UIA provider: nothing to ask.
        }

        return fromHandle is not null
            && (fromPoint is null || !Equals(fromHandle, fromPoint))
            && Judge(fromHandle, cursorIsIBeam);
    }

    /// <summary>Classifies one element, logging the facts when it is refused.</summary>
    private static bool Judge(AutomationElement element, bool cursorIsIBeam)
    {
        var facts = Describe(element);
        var verdict = IsTextTarget(facts, cursorIsIBeam);
        if (!verdict)
        {
            // Only refusals are logged, and at most one every few seconds: this runs on every
            // left press, and a user clicking around would otherwise fill app.log with one line
            // per click. The facts are what answers "why did the button not appear here".
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastRefusalLogAt);
            if (now - previous >= RefusalLogIntervalMs
                && Interlocked.CompareExchange(ref _lastRefusalLogAt, now, previous) == previous)
            {
                AppLog.Write(
                    $"Text target gate: type={facts.ControlType ?? "?"} "
                    + $"value={facts.SupportsValue}/{facts.ValueReadOnly} "
                    + $"text={facts.SupportsText}/{facts.TextReadOnly} "
                    + $"ibeam={cursorIsIBeam} -> false");
            }
        }

        return verdict;
    }

    private const long RefusalLogIntervalMs = 5000;

    private static long _lastRefusalLogAt;

    /// <summary>Reads the facts this gate needs out of a live element, tolerating provider errors.</summary>
    public static TextTargetFacts Describe(AutomationElement element)
    {
        string? controlType = null;
        try
        {
            controlType = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty);
        }
        catch (Exception)
        {
            return TextTargetFacts.None;
        }

        var supportsValue = false;
        var valueReadOnly = false;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
                && valuePattern is ValuePattern value)
            {
                supportsValue = true;
                valueReadOnly = value.Current.IsReadOnly;
            }
        }
        catch (Exception)
        {
            // A provider that throws on ValuePattern tells us nothing; fall through to TextPattern.
            supportsValue = false;
        }

        var supportsText = false;
        var textReadOnly = false;
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)
                && textPattern is TextPattern text)
            {
                supportsText = true;
                textReadOnly = IsReadOnlyText(text);
            }
        }
        catch (Exception)
        {
            supportsText = false;
        }

        return new TextTargetFacts(controlType, supportsValue, valueReadOnly, supportsText, textReadOnly);
    }

    /// <summary>
    /// Reads the IsReadOnly text attribute from the document range. TextPattern has no
    /// element-level read-only flag; the attribute lives on ranges, and <c>DocumentRange</c> is
    /// the cheapest range that answers for the whole control.
    /// </summary>
    private static bool IsReadOnlyText(TextPattern text)
    {
        try
        {
            var attribute = text.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            return attribute == TextPattern.MixedAttributeValue || attribute is true;
        }
        catch (Exception)
        {
            // A provider that cannot answer must not be treated as read-only: a plain Edit
            // control that simply does not implement the attribute would otherwise lose the
            // gesture entirely.
            return false;
        }
    }
}
