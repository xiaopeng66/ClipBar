using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using WindowsInput;
using ClipBar.App.Platform;

namespace ClipBar.App.Services;

/// <summary>Outcome of asking the foreground application what text is selected.</summary>
public readonly record struct SelectionProbeResult(bool HasText, string? Text)
{
    public static SelectionProbeResult None => new(false, null);
}

/// <summary>
/// Reads the current text selection from the foreground application.
/// <para>
/// The first attempt uses UI Automation's TextPattern; it is silent and never touches the
/// clipboard. Many custom-drawn apps do not expose TextPattern, so an optional Ctrl+C fallback
/// reads the selection through the clipboard and restores every format afterwards.
/// </para>
/// <para>
/// Every call is bounded: UIA is a cross-process COM call and can hang behind a busy
/// application, so it runs on a worker with a hard timeout. Failures return "no text" instead
/// of surfacing an error — the floating bar simply does not appear.
/// </para>
/// </summary>
public sealed class SelectionProbe
{
    /// <summary>Hard ceiling for a UIA query; a hung provider must not keep the probe alive.</summary>
    public static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(350);

    /// <summary>Ceiling for the Ctrl+C fallback's clipboard wait.</summary>
    public static readonly TimeSpan ClipboardTimeout = TimeSpan.FromMilliseconds(800);

    private static readonly SemaphoreSlim ClipboardFallbackGate = new(1, 1);
    private readonly InputSimulator _input = new();

    /// <summary>
    /// Reads the selection of <paramref name="foregroundWindow"/>. <paramref name="allowClipboardFallback"/>
    /// enables the Ctrl+C path for apps that do not expose TextPattern.
    /// </summary>
    public async Task<SelectionProbeResult> TryGetSelectionAsync(
        IntPtr foregroundWindow,
        bool allowClipboardFallback,
        CancellationToken cancellationToken = default)
    {
        var fromUia = await TryUiaAsync(foregroundWindow, cancellationToken).ConfigureAwait(false);
        if (fromUia.HasText || !allowClipboardFallback)
        {
            return fromUia;
        }

        return await TryClipboardAsync(foregroundWindow, cancellationToken).ConfigureAwait(false);
    }

    private static Task<SelectionProbeResult> TryUiaAsync(
        IntPtr foregroundWindow,
        CancellationToken cancellationToken)
        => BoundedUiAutomation.RunAsync(
            () => ReadUia(foregroundWindow),
            UiaTimeout,
            SelectionProbeResult.None,
            cancellationToken);

    private static SelectionProbeResult ReadUia(IntPtr foregroundWindow)
    {
        var element = TryElement(foregroundWindow);
        if (element is null)
        {
            return SelectionProbeResult.None;
        }

        var text = ReadTextPattern(element);
        if (text is null)
        {
            // The focused child (an edit control inside a window) is the element that owns the
            // selection in most apps; the top-level window usually has no TextPattern.
            var focused = TryFocusedElement();
            if (focused is not null && !Equals(focused, element))
            {
                text = ReadTextPattern(focused);
            }
        }

        // A whitespace-prefixed selection is still a real selection. Treating it as empty
        // falls through to Ctrl+C and copies the entire selected range into the global clipboard.
        if (!SelectionClipboardPolicy.HasSelectionText(text))
        {
            return SelectionProbeResult.None;
        }

        return new SelectionProbeResult(true, text!.Trim());
    }

    private static AutomationElement? TryElement(IntPtr hwnd)
    {
        try
        {
            return hwnd == IntPtr.Zero ? null : AutomationElement.FromHandle(hwnd);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AutomationElement? TryFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadTextPattern(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                || pattern is not TextPattern textPattern)
            {
                return null;
            }

            var ranges = textPattern.GetSelection();
            if (ranges.Length == 0)
            {
                return null;
            }

            // The floating-bar caller only needs to know whether a non-empty selection exists;
            // reading the whole selected range can allocate an arbitrarily large cross-process string.
            return ranges[0].GetText(1);
        }
        catch (Exception)
        {
            // Unsupported pattern, a provider that throws, a detached element.
            return null;
        }
    }

    /// <summary>
    /// Fallback for apps without TextPattern: press Ctrl+C, read what landed on the clipboard and
    /// put every original format back. Runs on an STA thread because both Clipboard and
    /// SendKeys require one.
    /// </summary>
    private async Task<SelectionProbeResult> TryClipboardAsync(
        IntPtr foregroundWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await ClipboardFallbackGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return SelectionProbeResult.None;
            }
        }
        catch (Exception)
        {
            return SelectionProbeResult.None;
        }

        var completion = new TaskCompletionSource<SelectionProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(CopyAndRead(foregroundWindow));
            }
            catch (Exception)
            {
                completion.TrySetResult(SelectionProbeResult.None);
            }
            finally
            {
                ClipboardFallbackGate.Release();
            }
        })
        {
            IsBackground = true,
            Name = "ClipBar.SelectionFallback",
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception)
        {
            ClipboardFallbackGate.Release();
            return SelectionProbeResult.None;
        }

        try
        {
            return await completion.Task.WaitAsync(ClipboardTimeout + TimeSpan.FromMilliseconds(200), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return SelectionProbeResult.None;
        }
    }

    private SelectionProbeResult CopyAndRead(IntPtr foregroundWindow)
    {
        IDataObject? backup = null;
        var sequenceBeforeSnapshot = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            backup = Clipboard.GetDataObject();
        }
        catch (Exception)
        {
            // A busy clipboard cannot be sampled safely; do not replace its current contents.
            return SelectionProbeResult.None;
        }

        var sequenceBeforeCopy = NativeMethods.GetClipboardSequenceNumber();
        if (SelectionClipboardPolicy.HasChanged(sequenceBeforeSnapshot, sequenceBeforeCopy)
            || foregroundWindow == IntPtr.Zero
            || NativeMethods.GetForegroundWindow() != foregroundWindow)
        {
            return SelectionProbeResult.None;
        }

        uint? probeSequence = null;
        try
        {
            _input.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_C);

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < ClipboardTimeout)
            {
                Thread.Sleep(40);
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                if (!SelectionClipboardPolicy.HasChanged(sequenceBeforeCopy, sequence))
                {
                    continue;
                }

                var owner = NativeMethods.GetClipboardOwner();
                if (owner == IntPtr.Zero)
                {
                    continue;
                }

                NativeMethods.GetWindowThreadProcessId(owner, out var ownerProcessId);
                NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var targetProcessId);
                if (targetProcessId == 0 || ownerProcessId != targetProcessId)
                {
                    // A different process updated the clipboard during the probe. Leave its data
                    // untouched rather than returning stale text or restoring over the new copy.
                    return SelectionProbeResult.None;
                }

                probeSequence = sequence;
                if (NativeMethods.GetForegroundWindow() != foregroundWindow)
                {
                    return SelectionProbeResult.None;
                }

                try
                {
                    if (!Clipboard.ContainsText())
                    {
                        continue;
                    }

                    var text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text)
                        && SelectionClipboardPolicy.MayRestore(sequence, NativeMethods.GetClipboardSequenceNumber()))
                    {
                        return new SelectionProbeResult(true, text.Trim());
                    }
                }
                catch (Exception)
                {
                    // Another process owns the clipboard right now; keep waiting briefly.
                }
            }

            return SelectionProbeResult.None;
        }
        finally
        {
            if (probeSequence is { } sequence
                && SelectionClipboardPolicy.MayRestore(sequence, NativeMethods.GetClipboardSequenceNumber()))
            {
                try
                {
                    if (backup is null)
                    {
                        Clipboard.Clear();
                    }
                    else
                    {
                        Clipboard.SetDataObject(backup, copy: true);
                    }
                }
                catch (Exception)
                {
                    // Restoring is best effort: a locked clipboard must not break the gesture.
                }
            }
        }
    }
}
