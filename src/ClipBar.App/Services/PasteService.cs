using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipBar.App.Platform;
using ClipBar.Core.Models;
using WindowsInput;
using System.IO;

namespace ClipBar.App.Services;

/// <summary>What actually happened to the clipboard, so the caller can tell the truth to the user.</summary>
public enum PasteOutcome
{
    /// <summary>Content reached the clipboard and Ctrl+V was sent.</summary>
    Pasted,

    /// <summary>Content reached the clipboard; the keystroke was withheld (paste disabled or the
    /// target window refused activation). The user can still press Ctrl+V themselves.</summary>
    CopiedOnly,

    /// <summary>Nothing reached the clipboard, so no keystroke was sent.</summary>
    CopyFailed,

    /// <summary>The target or clipboard changed before Ctrl+V, so automatic paste was cancelled.</summary>
    Cancelled,
}

public sealed class PasteService
{
    private readonly Action<TimeSpan, string?> _suppress;
    private readonly InputSimulator _input = new();
    private readonly PasteTransactionGate _pasteTransactionGate = new();

    public PasteService(ClipboardMonitor monitor)
        : this(monitor.Suppress)
    {
    }

    /// <summary>Seam for tests: the clipboard monitor's suppression is the only thing we need from it.</summary>
    public PasteService(Action<TimeSpan, string?> suppress)
    {
        _suppress = suppress;
    }

    /// <summary>
    /// Why <paramref name="item"/> cannot reach the clipboard at all, or null when it can. Pure, so
    /// the "never paste stale content" rule is testable without a live clipboard.
    /// </summary>
    public static string? DescribeUncopyable(ClipItem item, Func<string, bool> fileExists)
    {
        switch (item.Kind)
        {
            case ClipKind.Text:
                return null;
            case ClipKind.Image:
                if (string.IsNullOrWhiteSpace(item.ImagePath))
                {
                    return "image clip has no path";
                }

                return fileExists(item.ImagePath) ? null : "image file is gone";
            case ClipKind.Files:
                return item.FilePaths.Count == 0 ? "file list is empty" : null;
            default:
                return $"unknown clip kind {item.Kind}";
        }
    }

    /// <summary>
    /// Puts <paramref name="item"/> on the clipboard. Returns false when nothing was written — an
    /// image whose file has since been trimmed away, an empty file list, or every attempt losing
    /// the clipboard race. The caller must not paste in that case: a blind Ctrl+V would inject
    /// whatever the clipboard held *before* (often the password the user copied last).
    /// </summary>
    /// <summary>
    /// Whether this write should include the HTML flavour. "Paste as plain text" (A1) and
    /// clips without HTML both skip it, so the target editor receives unformatted text.
    /// </summary>
    public static bool ShouldWriteHtml(ClipItem item, bool plainTextOnly)
        => !plainTextOnly && !string.IsNullOrWhiteSpace(item.Html);

    public async Task<bool> SetClipboardAsync(ClipItem item, bool plainTextOnly = false)
    {
        // The hash lets the monitor filter our own write out after the window expires while still
        // capturing a genuine user copy made inside it (G-10).
        _suppress(TimeSpan.FromMilliseconds(700), item.Hash);

        if (DescribeUncopyable(item, File.Exists) is { } problem)
        {
            AppLog.Write("Paste: " + problem + ", nothing copied");
            return false;
        }

        // Decode the image once, before the clipboard retry loop. Measured: a damaged, truncated
        // or non-PNG file throws NotSupportedException / FileFormatException out of EndInit, and a
        // malformed path throws UriFormatException — none of them a COMException, so all of them
        // used to escape this method. By then the panel had already hidden and the caller is
        // async void with no catch, so the user got no paste and no message. Decoding outside the
        // loop also means a retry re-runs the clipboard write only, not the whole decode.
        BitmapImage? image = null;
        if (item.Kind == ClipKind.Image)
        {
            image = TryDecodeImage(item.ImagePath!);
            if (image is null)
            {
                AppLog.Write("Paste: image file could not be decoded, nothing copied");
                return false;
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Text:
                    {
                        var data = new DataObject();
                        data.SetData(DataFormats.UnicodeText, item.Text ?? string.Empty);
                        if (ShouldWriteHtml(item, plainTextOnly))
                        {
                            data.SetData(DataFormats.Html, HtmlFormatHelper.Build(item.Html!, item.Text));
                        }

                        Clipboard.SetDataObject(data, copy: true);
                        return true;
                    }
                    case ClipKind.Image:
                    {
                        var data = new DataObject();
                        data.SetImage(image!);
                        Clipboard.SetDataObject(data, copy: true);
                        return true;
                    }
                    case ClipKind.Files:
                    {
                        // SetFileDropList copies by default (verified: the data survives the
                        // writing process exiting), so it needs no explicit copy: true.
                        var files = new StringCollection();
                        files.AddRange(item.FilePaths.ToArray());
                        Clipboard.SetFileDropList(files);
                        return true;
                    }
                }
            }
            catch (COMException)
            {
            }
            catch (ExternalException)
            {
            }

            await Task.Delay(40).ConfigureAwait(true);
        }

        AppLog.Write("Paste: clipboard stayed locked, gave up after 5 attempts");
        return false;
    }

    /// <summary>
    /// Decodes the clip's PNG, or null when the file is unreadable as an image. Measured against
    /// three damaged shapes (0-byte, truncated PNG header, plain text named .png): they throw
    /// <c>NotSupportedException</c> / <c>FileFormatException</c>, never the COM exceptions the
    /// clipboard loop catches. <c>File.Exists</c> returns true for all of them, so the
    /// <see cref="DescribeUncopyable"/> gate cannot catch this — the decode has to.
    /// </summary>
    private static BitmapImage? TryDecodeImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            // The set is wide on purpose: UriFormatException for a malformed path, the two above
            // for a bad file, IOException for a file locked or deleted between the Exists check
            // and the read. Any of them means "this image cannot be put on the clipboard".
            AppLog.Error("Paste: decode image", ex);
            return null;
        }
    }

    /// <summary>
    /// Copies the clip to the clipboard and, when <paramref name="paste"/> is true, sends Ctrl+V
    /// to <paramref name="targetWindow"/>. Calls are serialized across all UI surfaces so one
    /// selection cannot replace another between its clipboard write and paste keystroke.
    /// </summary>
    public async Task<PasteOutcome> PasteAsync(
        ClipItem item,
        IntPtr targetWindow,
        bool paste,
        IntPtr selfWindow = default,
        uint selfProcessId = 0,
        bool plainTextOnly = false,
        bool restoreClipboardAfterPaste = false)
    {
        return await _pasteTransactionGate.RunAsync(() => PasteCoreAsync(
            item, targetWindow, paste, selfWindow, selfProcessId, plainTextOnly, restoreClipboardAfterPaste))
            .ConfigureAwait(true);
    }

    private async Task<PasteOutcome> PasteCoreAsync(
        ClipItem item,
        IntPtr targetWindow,
        bool paste,
        IntPtr selfWindow,
        uint selfProcessId,
        bool plainTextOnly,
        bool restoreClipboardAfterPaste)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!await SetClipboardAsync(item, plainTextOnly).ConfigureAwait(true))
        {
            return PasteOutcome.CopyFailed;
        }
        AppLog.Write($"Paste: clipboard written at {watch.ElapsedMilliseconds} ms");
        var clipboardSequence = NativeMethods.GetClipboardSequenceNumber();

        if (!paste)
        {
            return PasteOutcome.CopiedOnly;
        }

        // Never inject keys into "unknown" (zero) or into our own window: the capture can lose a
        // race with a focus transition and hold the panel's own hwnd. Both downgrade to a plain
        // copy the user can paste manually — a blind Ctrl+V would inject the *previous* clipboard
        // content, which is exactly what F-39 exists to prevent.
        NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetPid);
        if (targetWindow == IntPtr.Zero
            || targetWindow == selfWindow
            || (selfProcessId != 0 && targetPid == selfProcessId))
        {
            AppLog.Write($"Paste: unsafe target (hwnd=0x{targetWindow.ToInt64():X}), copied without Ctrl+V");
            return PasteOutcome.CopiedOnly;
        }

        // The clipboard write above can succeed across integrity levels, but SendInput/UIA
        // cannot: UIPI blocks a normal-integrity process from injecting Ctrl+V into an elevated
        // one. Keep the copied content and withhold only the keystroke.
        if (ElevationPolicy.MustWithholdPaste(
                Elevation.IsWindowElevated(targetWindow),
                Elevation.IsSelfElevated()))
        {
            AppLog.Write("Paste: target is elevated and ClipBar is not; copied without Ctrl+V");
            return PasteOutcome.CopiedOnly;
        }

        if (!await TryActivateAsync(targetWindow).ConfigureAwait(true))
        {
            AppLog.Write("Paste: target window refused activation, skipped Ctrl+V");
            return PasteOutcome.CopiedOnly;
        }

        // Wait for unrelated keys to be released before sending Ctrl+V: a chord fired while a
        // real key is down can reach the target as a different combination (Ctrl+V+Space in a
        // media player may toggle something else). Use a bounded 800 ms wait.
        var keyWaitStart = watch.ElapsedMilliseconds;
        for (var attempt = 0; attempt < 8 && NativeMethods.IsAnyOtherKeyDown(); attempt++)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }
        AppLog.Write($"Paste: key-wait took {watch.ElapsedMilliseconds - keyWaitStart} ms");

        // Give the target a beat to restore its focused child: a popup that was just closed can
        // leave the window active while its edit control is not yet the focus target, and the
        // Ctrl+V then lands nowhere (measured with the compact list over a WinForms text box).
        await Task.Delay(60).ConfigureAwait(true);
        if (!PasteClipboardPolicy.MaySendPaste(
                targetWindow,
                NativeMethods.GetForegroundWindow(),
                clipboardSequence,
                NativeMethods.GetClipboardSequenceNumber()))
        {
            AppLog.Write("Paste: foreground or clipboard changed before Ctrl+V; paste cancelled");
            return PasteOutcome.Cancelled;
        }

        try
        {
            _input.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
            AppLog.Write($"Paste: Ctrl+V sent at {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            AppLog.Error("Send paste", ex);
            return PasteOutcome.CopiedOnly;
        }

        // Keep the selected text on the clipboard after the target has had time to consume it.
        // The delayed restore runs on WPF's STA dispatcher and only if no later clipboard write
        // replaced this paste's generation.
        if (restoreClipboardAfterPaste)
        {
            _ = RestoreClipboardAsync(item, clipboardSequence);
        }

        return PasteOutcome.Pasted;
    }

    /// <summary>
    /// Keeps the selected plain text available on the clipboard after the paste. Runs the WPF
    /// clipboard call on the STA dispatcher and leaves any newer clipboard generation untouched.
    /// </summary>
    private async Task RestoreClipboardAsync(ClipItem item, uint pasteSequence)
    {
        if (item.Kind != ClipKind.Text || string.IsNullOrEmpty(item.Text))
        {
            return;
        }

        try
        {
            await Task.Delay(1200).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                if (!PasteClipboardPolicy.MayRestore(pasteSequence, NativeMethods.GetClipboardSequenceNumber()))
                {
                    return;
                }

                _suppress(TimeSpan.FromMilliseconds(700), item.Hash);
                Clipboard.SetText(item.Text);
            }).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Restore clipboard after paste", ex);
        }
    }

    /// <summary>
    /// Brings <paramref name="window"/> to the foreground and confirms it actually got there.
    /// SetForegroundWindow is refused while another process owns the foreground (the shell's
    /// foreground lock), which is the common case right after our panel closes; attaching to the
    /// foreground thread's input queue is the documented way around it.
    /// </summary>
    private static async Task<bool> TryActivateAsync(IntPtr window)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == window)
            {
                if (attempt > 0)
                {
                    AppLog.Write($"Paste: target activated on attempt {attempt + 1}");
                }

                return true;
            }

            var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            var currentThread = NativeMethods.GetCurrentThreadId();
            var attached = foregroundThread != 0
                && foregroundThread != currentThread
                && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

            try
            {
                NativeMethods.BringWindowToTop(window);
                NativeMethods.SetForegroundWindow(window);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            // The window manager applies the switch asynchronously; on a slow box the shell can
            // still be restoring focus during the early checks (G-09), so the backoff now spans
            // ~700 ms instead of 210 ms.
            await Task.Delay(60 + attempt * 40).ConfigureAwait(true);
            if (NativeMethods.GetForegroundWindow() == window)
            {
                if (attempt > 0)
                {
                    AppLog.Write($"Paste: target activated on attempt {attempt + 1}");
                }

                return true;
            }
        }

        return false;
    }
}