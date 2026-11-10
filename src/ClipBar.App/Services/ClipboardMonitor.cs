using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using System.IO;

namespace ClipBar.App.Services;

public sealed class ClipboardMonitor : IDisposable
{
    /// <summary>Longest clip text we store. A larger paste is truncated, not rejected.</summary>
    public const int MaxTextLength = 1024 * 1024;

    /// <summary>Most file paths we store for one CF_HDROP clip.</summary>
    public const int MaxFilePaths = 200;

    private readonly IClipRepository _repository;
    private readonly Func<AppSettings> _settings;
    private readonly Action<ClipItem> _onCaptured;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;
    /// <summary>
    /// Session-scoped capture pause from the tray menu. Deliberately not persisted: a forgotten
    /// pause would silently stop recording for weeks.
    /// </summary>
    public bool IsPaused { get; set; }

    private long _suppressUntil;
    private string? _suppressedWriteHash;
    private string? _pendingSourceProcessName;
    private uint _pendingClipboardSequence;
    private bool _hasPendingClipboardSource;
    private bool _disposed;

    /// <summary>Normal debounce for a clipboard update; the post-suppression re-check uses its own.</summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(140);

    public ClipboardMonitor(
        IClipRepository repository,
        Func<AppSettings> settings,
        Action<ClipItem> onCaptured)
    {
        _repository = repository;
        _settings = settings;
        _onCaptured = onCaptured;

        var parameters = new HwndSourceParameters("ClipBar.Listener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DebounceInterval,
        };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await CaptureAsync().ConfigureAwait(true);
        };

        if (!NativeMethods.AddClipboardFormatListener(_source.Handle))
        {
            AppLog.Write("AddClipboardFormatListener failed: " + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Mutes capture for <paramref name="duration"/> — normally because this app just wrote the
    /// clipboard itself. The write's <paramref name="writtenHash"/> lets the post-window check
    /// drop exactly that change while still capturing a genuine user copy made inside the window
    /// (G-10: the old behaviour dropped it forever).
    /// </summary>
    public void Suppress(TimeSpan duration, string? writtenHash = null)
    {
        _suppressUntil = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        _suppressedWriteHash = writtenHash;

        // Re-check at expiry even when no further WM_CLIPBOARDUPDATE arrives: without this a
        // user copy inside the window never fires a capture again.
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(duration.TotalMilliseconds + 60);
        _debounce.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            var ownerProcess = GetProcessName(NativeMethods.GetClipboardOwner());
            var foregroundProcess = GetForegroundProcessName();
            _pendingSourceProcessName = ClipboardSourcePolicy.ResolveSource(ownerProcess, foregroundProcess);
            _pendingClipboardSequence = sequence;
            _hasPendingClipboardSource = true;
            if (sequence != NativeMethods.GetClipboardSequenceNumber())
            {
                // The clipboard changed while attribution was sampled. CaptureAsync will notice
                // the stale sequence and sample the current owner rather than trust this identity.
                _pendingSourceProcessName = null;
            }

            _debounce.Stop();
            _debounce.Interval = DebounceInterval;
            _debounce.Start();
        }

        return IntPtr.Zero;
    }

    private async Task CaptureAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsPaused)
        {
            _hasPendingClipboardSource = false;
            _pendingSourceProcessName = null;
            return;
        }

        var now = Environment.TickCount64;
        if (now < _suppressUntil)
        {
            // An update inside the suppression window: our own paste write is on its way, but a
            // user copy can also land here. Re-check once the window expires instead of dropping
            // the change forever (G-10); the hash filter below tells the two apart.
            _debounce.Stop();
            _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(DebounceInterval.TotalMilliseconds, _suppressUntil - now) + 60);
            _debounce.Start();
            return;
        }
        _debounce.Interval = DebounceInterval;

        if (!_hasPendingClipboardSource)
        {
            return;
        }

        var captureSequence = NativeMethods.GetClipboardSequenceNumber();
        var sourceProcess = _pendingClipboardSequence == captureSequence
            ? _pendingSourceProcessName
            : ClipboardSourcePolicy.ResolveSource(
                GetProcessName(NativeMethods.GetClipboardOwner()),
                GetForegroundProcessName());
        _hasPendingClipboardSource = false;
        _pendingSourceProcessName = null;
        if (captureSequence != NativeMethods.GetClipboardSequenceNumber())
        {
            return;
        }

        var settings = _settings();
        if (ClipboardSourcePolicy.IsIgnored(sourceProcess, settings))
        {
            // This clipboard generation cannot be captured. A paste's hash marker must not remain
            // armed and suppress a later user copy of the same text.
            _suppressedWriteHash = null;
            return;
        }

        try
        {
            var data = await ReadDataObjectAsync().ConfigureAwait(true);
            if (data is null || captureSequence != NativeMethods.GetClipboardSequenceNumber())
            {
                return;
            }

            await ImageReferenceMutationGate.Semaphore.WaitAsync().ConfigureAwait(true);
            try
            {
                // Re-check after waiting: a newer clipboard generation must not be stored from a
                // stale IDataObject while another image mutation was in progress.
                if (captureSequence != NativeMethods.GetClipboardSequenceNumber())
                {
                    return;
                }

                var item = await BuildItemAsync(data, settings, sourceProcess).ConfigureAwait(true);
                if (item is null || captureSequence != NativeMethods.GetClipboardSequenceNumber())
                {
                    return;
                }

                // The clipboard may still hold what our own paste wrote (G-10): skip exactly that
                // one, then clear the marker so a later genuine user copy of the same content is kept.
                if (_suppressedWriteHash is { } writtenHash)
                {
                    _suppressedWriteHash = null;
                    if (string.Equals(item.Hash, writtenHash, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                var stored = await _repository.AddOrTouchAsync(item).ConfigureAwait(true);

                // No "delete the superseded image" step here: ImageStore names files after the
                // content hash, so a duplicate clip resolves to the very same PNG and never writes
                // a second one. Leftovers from a crash between writing and inserting are collected
                // by ImageStore.SweepOrphans at startup.
                var removed = await _repository.TrimAsync(settings.MaxItems).ConfigureAwait(true);
                foreach (var path in removed)
                {
                    ImageStore.TryDelete(path);
                }

                _onCaptured(stored);
            }
            finally
            {
                ImageReferenceMutationGate.Semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture clip", ex);
        }
    }

    private static async Task<IDataObject?> ReadDataObjectAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is not null)
                {
                    return data;
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

        return null;
    }

    /// <summary>
    /// Applies <see cref="MaxTextLength"/>. Split out of the capture path so the cap itself is
    /// testable without a live clipboard: a 50 MB paste must come back as exactly the first
    /// <see cref="MaxTextLength"/> characters, with no exception and no second copy of the text.
    /// </summary>
    public static (string Text, bool Truncated) ApplyTextCap(string text)
        => text.Length > MaxTextLength
            ? (text[..MaxTextLength], true)
            : (text, false);

    /// <summary>
    /// Applies <see cref="MaxFilePaths"/>, reporting how many paths were dropped. A shell
    /// selection of 10,000 files must not turn one row into a megabyte of JSON.
    /// </summary>
    public static (List<string> Kept, int Dropped) ApplyFileCap(IReadOnlyList<string> files)
        => files.Count > MaxFilePaths
            ? (files.Take(MaxFilePaths).ToList(), files.Count - MaxFilePaths)
            : (files.ToList(), 0);

    private static async Task<ClipItem?> BuildItemAsync(
        IDataObject data,
        AppSettings settings,
        string? sourceProcess)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (settings.CaptureFiles && data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = GetFilePaths(data);
            if (files.Count > 0)
            {
                // A shell selection can hold thousands of paths; storing them all would make
                // one row megabytes wide and slow every query. Keep the first N and say so.
                var (kept, dropped) = ApplyFileCap(files);
                if (dropped > 0)
                {
                    AppLog.Write($"剪贴板文件数 {files.Count} 超过上限 {MaxFilePaths}，只保留前 {MaxFilePaths} 个。");
                    files = kept;
                }

                var item = new ClipItem
                {
                    Kind = ClipKind.Files,
                    Text = string.Join(Environment.NewLine, files),
                    FilePaths = files,
                    SourceApp = sourceProcess,
                    CreatedAt = now,
                    SizeBytes = files.Sum(path => SafeFileSize(path)),
                };
                item.Hash = ClipHash.Compute(item);
                return item;
            }
        }

        var text = GetText(data);
        if (!string.IsNullOrEmpty(text))
        {
            // Same reasoning for text: an unbounded clip (a whole log file) bloats the DB and
            // the list preview. Truncate at a generous cap instead of refusing the clip.
            var (keptText, truncated) = ApplyTextCap(text);
            if (truncated)
            {
                AppLog.Write($"剪贴板文本长度 {text.Length} 超过上限 {MaxTextLength}，已截断。");
                text = keptText;
            }

            var html = settings.CaptureHtml ? HtmlFormatHelper.ExtractFragment(GetHtml(data)) : null;
            if (html is not null && html.Length > MaxTextLength)
            {
                html = html[..MaxTextLength];
            }

            var item = new ClipItem
            {
                Kind = ClipKind.Text,
                Text = text,
                Html = html,
                SourceApp = sourceProcess,
                CreatedAt = now,
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
            };
            item.Hash = ClipHash.Compute(item);
            return item;
        }

        if (settings.CaptureImages)
        {
            var bitmap = GetBitmap(data);
            if (bitmap is not null)
            {
                var saved = await ImageStore.SavePngAsync(bitmap, AppPaths.ImagesDirectory).ConfigureAwait(true);
                return new ClipItem
                {
                    Kind = ClipKind.Image,
                    Text = $"图片 {saved.Width}×{saved.Height}",
                    ImagePath = saved.Path,
                    ImageWidth = saved.Width,
                    ImageHeight = saved.Height,
                    SizeBytes = saved.SizeBytes,
                    Hash = saved.Hash,
                    SourceApp = sourceProcess,
                    CreatedAt = now,
                };
            }
        }

        return null;
    }

    private static string? GetText(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                return data.GetData(DataFormats.UnicodeText) as string;
            }

            if (data.GetDataPresent(DataFormats.Text))
            {
                return data.GetData(DataFormats.Text) as string;
            }
        }
        catch (COMException)
        {
        }

        return null;
    }

    private static string? GetHtml(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("HTML Format"))
            {
                return data.GetData("HTML Format") as string;
            }
        }
        catch (COMException)
        {
        }

        return null;
    }

    private static BitmapSource? GetBitmap(IDataObject data)
    {
        try
        {
            // Prefer the IDataObject we already hold: Clipboard.GetImage() re-opens the global
            // clipboard, so it can fail with a busy clipboard or return a newer generation than
            // the one this capture was built from.
            if (data.GetDataPresent(DataFormats.Bitmap)
                && data.GetData(DataFormats.Bitmap) is BitmapSource source)
            {
                if (!source.IsFrozen && source.CanFreeze)
                {
                    source.Freeze();
                }

                return source;
            }

            var image = Clipboard.GetImage();
            if (image is not null)
            {
                if (!image.IsFrozen && image.CanFreeze)
                {
                    image.Freeze();
                }

                return image;
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<string> GetFilePaths(IDataObject data)
    {
        try
        {
            var value = data.GetData(DataFormats.FileDrop);
            return value switch
            {
                string[] array => [.. array],
                System.Collections.Specialized.StringCollection collection => [.. collection.Cast<string>()],
                _ => [],
            };
        }
        catch (COMException)
        {
            return [];
        }
    }

    private static bool IsNetworkPath(string? path)
        => ClipboardPathPolicy.IsNetworkPath(path);

    /// <summary>
    /// Size of one path in a file-list clip, or 0 when it cannot be read. Must never throw: the
    /// caller sums this over the whole selection, so anything escaping here would abort the sum and
    /// be swallowed by <c>CaptureAsync</c>'s outer catch, losing the entire clip instead of one
    /// size field.
    /// <para>
    /// Measured (not assumed): <see cref="File.Exists"/> never throws — it returns false for an
    /// empty string, an embedded NUL, a bare device prefix, a nonexistent drive and a null argument.
    /// So <c>new FileInfo(...)</c> is only reached for a path that existed a moment ago, and the
    /// hostile-payload case is unreachable. <see cref="IOException"/> (which already covers
    /// <see cref="FileNotFoundException"/> and <see cref="PathTooLongException"/>) is the reachable
    /// one; the other two catches are belt-and-braces for the narrow window where the file is
    /// deleted or its ACL changes between the check and the read.
    /// </para>
    /// Public so this rule is testable without a live clipboard.
    /// </summary>
    public static long SafeFileSize(string path)
    {
        if (IsNetworkPath(path))
        {
            return 0;
        }

        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static string? GetForegroundProcessName()
        => GetProcessName(NativeMethods.GetForegroundWindow());

    private static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Stop();
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}