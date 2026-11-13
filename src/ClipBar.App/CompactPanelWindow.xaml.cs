using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using Wpf.Ui.Controls;

namespace ClipBar.App;

/// <summary>
/// The compact clipboard list the floating bar opens on hover: recent clips only — no search, no
/// filters, no footer actions — anchored next to the pointer. Clicking a row pastes it into the
/// window that was in front when the list appeared, which is what the system clipboard view
/// does. It is deliberately a plain Window with its own surface (same as the other popups), so
/// a missing theme dictionary cannot paint a native caption over it.
/// </summary>
public partial class CompactPanelWindow : Window
{
    /// <summary>
    /// Ceiling for one load; the real limit comes from the user's "最多保存" setting, so the
    /// compact list shows the same history as the panel rather than an arbitrary 12 rows.
    /// </summary>
    private const int MaxRowsCeiling = 5000;

    private const double ScreenMargin = 6;

    /// <summary>One compact row: the 32 DIP button plus its 2 DIP margin and 1 DIP hairline.</summary>
    private const double RowHeight = 35;

    /// <summary>Rows that fit the 420 DIP viewport; matches the ScrollViewer's MaxHeight.</summary>
    private const int MaxVisibleRows = 12;

    private readonly IClipRepository _repository;
    private readonly PasteService _pasteService;
    private readonly Func<AppSettings> _settings;
    private readonly double _preferredWidth;
    private double _preferredHeight = 260;
    private IntPtr _target;
    private bool _closing;
    private bool _pasting;

    /// <summary>
    /// How long the hide fade runs before <see cref="Window.Hide"/>. The window stays hit-testable
    /// during the fade so a click that dismisses the bar is not delivered to the window underneath.
    /// </summary>
    private const int HideFadeMs = 45;

    /// <summary>Click-to-paste span, for the timing line in the log.</summary>
    private readonly System.Diagnostics.Stopwatch _pasteWatch = new();

    /// <summary>Fades the window out while it stays on screen, so nothing re-hit-tests beneath it.</summary>
    private void AnimateHide()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // A layered-window alpha fade: the window stays on screen but reads as gone, and no
        // re-hit-test can deliver a stray click under the released button. WPF's
        // AllowsTransparency=false windows need the style bit set before SetLayeredWindowAttributes
        // accepts the call.
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if ((style & NativeMethods.WsExLayered) == 0)
        {
            NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GwlExStyle,
                new IntPtr(style | NativeMethods.WsExLayered));
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LwaAlpha);
    }

    /// <summary>
    /// Set when the floating bar opens the list: the list must not activate, or the app the user
    /// was typing in loses focus mid-gesture. The coordinator then owns dismissal for both
    /// windows, so no per-window outside-click hook is installed.
    /// </summary>
    private bool _noActivate;

    private bool _managedByCoordinator;

    public static readonly DependencyProperty KeyboardSelectionEnabledProperty =
        DependencyProperty.Register(
            nameof(KeyboardSelectionEnabled),
            typeof(bool),
            typeof(CompactPanelWindow),
            new PropertyMetadata(false));

    /// <summary>True when this is the activated picker opened by --compact or a second launch.</summary>
    public bool KeyboardSelectionEnabled
    {
        get => (bool)GetValue(KeyboardSelectionEnabledProperty);
        private set => SetValue(KeyboardSelectionEnabledProperty, value);
    }

    /// <summary>Non-null when the gesture started in ClipBar's own search box. A row click then hands the
    /// clip to the panel for direct insertion instead of sending Ctrl+V to our own process.
    /// </summary>
    private Func<ClipItem, bool>? _insertIntoOwnTextInput;

    /// <summary>True once the rows are bound (see <see cref="PrepareAsync"/>).</summary>
    private bool _rowsLoaded;

    /// <summary>True when <see cref="PrepareAsync"/> already measured the content height.</summary>
    private bool _heightPreMeasured;

    /// <summary>
    /// Safety net for the case where Activate() did not take (another app holds the foreground):
    /// Deactivated would never fire, so a click outside is watched directly.
    /// </summary>
    private OutsideClickHook? _outsideClick;

    public CompactPanelWindow(
        IClipRepository repository,
        PasteService pasteService,
        Func<AppSettings> settings)
    {
        InitializeComponent();
        _preferredWidth = Width;
        _repository = repository;
        _pasteService = pasteService;
        _settings = settings;
        SourceInitialized += (_, _) => ApplyToolWindowStyle();

        Closed += (_, _) =>
        {
            _closing = true;
            _outsideClick?.Dispose();
            _outsideClick = null;
        };
    }

    /// <summary>Fills the list and shows the popup next to the cursor.</summary>
    public async Task ShowAtCursorAsync()
    {
        KeyboardSelectionEnabled = true;

        // Capture the paste target BEFORE we are shown: once this window is up it owns the
        // foreground, and the clip must land in the app the user came from.
        var foreground = NativeMethods.GetForegroundWindow();
        var target = foreground;
        if (!PasteTarget.WorthCapturing(foreground, SelfProcessId, PidOf))
        {
            // A launcher window can be the foreground; fall back to the previous window this
            // app tracked when that happens.
            target = _lastKnownTarget;
        }

        await ShowCoreAsync(target, anchor: null, noActivate: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the list below the floating bar without activating (the target app keeps focus),
    /// anchored to <paramref name="anchor"/> and pasting into <paramref name="target"/>.
    /// <paramref name="insertIntoOwnTextInput"/> is set when the gesture started in ClipBar's own
    /// search box: the row must then be inserted directly, because a Ctrl+V aimed at our own
    /// process is refused by the self-paste guard.
    /// </summary>
    public async Task ShowBelowAsync(
        Window anchor,
        IntPtr target,
        IReadOnlyList<ClipItem>? preloaded = null,
        Func<ClipItem, bool>? insertIntoOwnTextInput = null)
    {
        _managedByCoordinator = true;
        _insertIntoOwnTextInput = insertIntoOwnTextInput;

        // Not activated on show. The comment below used to require it, but the requirement was
        // wrong: PasteService re-activates the captured target itself before sending Ctrl+V
        // (attaching to the foreground thread's input queue), so the paste lands correctly from a
        // never-activated list. Activating here stole the foreground every time a hover opened the
        // list, which cancelled an in-place rename (Explorer's F2) the moment its window lost
        // activation. The own-search-box case was already the exception for the same reason.
        var noActivate = true;
        await ShowCoreAsync(target, anchor, noActivate, preloaded).ConfigureAwait(true);
    }

    /// <summary>True when the pointer is over the list; the coordinator keeps it open for this.</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <summary>
    /// Marks the window as owned by the floating-bar coordinator, which decides when it closes.
    /// Set when the window is prepared, i.e. before it is ever shown.
    /// </summary>
    public bool ManagedByCoordinator
    {
        get => _managedByCoordinator;
        set => _managedByCoordinator = value;
    }

    /// <summary>
    /// Builds the rows and creates the HWND without showing the window, so a later
    /// <see cref="ShowBelowAsync"/> is only a position + show. The coordinator calls this the
    /// moment the floating bar appears: moving the pointer onto the bar takes long enough to hide
    /// this work entirely, which is what makes hover-to-open feel instant.
    /// </summary>
    public async Task PrepareAsync(IntPtr target, IReadOnlyList<ClipItem>? preloaded = null)
    {
        if (_rowsLoaded)
        {
            return;
        }

        _target = target;

        var rows = preloaded;
        if (rows is null)
        {
            var limit = Math.Clamp(_settings().MaxItems, 10, MaxRowsCeiling);
            rows = await _repository
                .QueryAsync(ClipFilter.All, search: null, limit: limit)
                .ConfigureAwait(true);
        }

        // Rows are plain data; the ListBox builds only the ones inside the viewport. Building each
        // row as real WPF elements cost ~200 ms per 60 rows, so a 400-clip history kept the UI
        // thread busy for 1.2 s after the list was already on screen.
        // The first row is marked so its separator (drawn on the row's top edge) is skipped.
        RowList.ItemsSource = rows
            .Select((row, index) => new CompactRow(row, index == 0))
            .ToList();
        RowList.SelectedIndex = -1;

        // The list needs a finite height, or the window's SizeToContent measures it with infinite
        // height, the virtualising panel realises every row to answer, and the whole point is lost
        // (measured: 805 ms for 400 rows, slower than building them by hand).
        _visibleRows = Math.Min(rows.Count, MaxVisibleRows);
        RowList.Height = _visibleRows * RowHeight;

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FooterText.Text = rows.Count == 0
            ? "复制点内容再来"
            : KeyboardSelectionEnabled
                ? $"{rows.Count} 条 · ↑/↓ 选择 · Enter 粘贴 · Esc 关闭"
                : $"{rows.Count} 条 · 点击即粘贴 · Esc 关闭";

        _rowsLoaded = true;

        // Create the HWND now. The first Show() on a window with no handle builds the window
        // manager object and pays the whole template/layout pass — measured at ~45 ms right on
        // the hover path. With the handle in place, Show() reuses it.
        new WindowInteropHelper(this).EnsureHandle();

        // Measure once here as well: the height depends only on the content, and the content is
        // final. PlaceBelow then only computes coordinates (measured: the measure pass was the
        // other ~28 ms on the hover path).
        MeasurePreferredSize();
    }

    /// <summary>Rows the viewport is sized for; set by <see cref="PrepareAsync"/>.</summary>
    private int _visibleRows;

    private void MeasurePreferredSize()
    {
        if (_heightPreMeasured || Content is not FrameworkElement content)
        {
            return;
        }
        content.Measure(new Size(_preferredWidth, double.PositiveInfinity));
        var desired = Math.Ceiling(content.DesiredSize.Height);
        if (desired > 0)
        {
            Height = _preferredHeight = desired;
            _heightPreMeasured = true;
        }
    }

    /// <summary>WPF owns sizing; native placement remains strictly position-only.</summary>
    internal (double Width, double Height) FitToWorkArea(double workWidth, double workHeight, double scale)
    {
        MeasurePreferredSize();
        PanelPlacement.ConstrainToWorkArea(this, workWidth, workHeight, scale, margin: ScreenMargin);
        var fitted = PanelPlacement.FitSize(_preferredWidth, PreferredHeightAtScale(scale),
            workWidth, workHeight, scale, ScreenMargin);
        Width = fitted.Width;
        Height = fitted.Height;
        BoundViewportToHeight(fitted.Height);
        return fitted;
    }

    /// <summary>
    /// Sizes the viewport to a whole number of rendered rows.
    /// <para>
    /// <see cref="RowHeight"/> is the template's DIP geometry, but what the layout actually draws
    /// is a whole number of device pixels: the 42 DIP button plus its 1 DIP separator is 53.75 px
    /// at 125% and snaps down to about 53, so nine rows take 477 px while nine times 43 DIP is
    /// 484. The spare seven pixels showed the next row's separator as a lone line at the bottom of
    /// the panel — the row that "was cut down to just its divider".
    /// </para>
    /// <para>
    /// The scroll host cannot answer this: it counts its viewport in whole items, so the overflow
    /// is invisible to it. Computing the row in device pixels and flooring it is what the layout
    /// itself does.
    /// </para>
    /// </summary>
    private void FitViewportToRows()
    {
        Height = Math.Min(PreferredHeightAtScale(VisualTreeHelper.GetDpi(this).DpiScaleY), MaxHeight);
        BoundViewportToHeight(Height);
    }

    private double PreferredHeightAtScale(double scale)
    {
        if (_visibleRows <= 0)
        {
            return _preferredHeight;
        }
        var targetScale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        var rowPixels = Math.Max(1, Math.Floor(RowHeight * targetScale));
        // One device pixel of slack. A row draws its hairline on its top edge, so sizing the
        // viewport to an exact row multiple can leave that first pixel of the next row inside
        // the viewport when DIPs map to fractional pixels (35 DIP -> 43.75 px at 125%). The
        // leaked pixel reads as a stray divider under the last full row.
        var viewportPixels = Math.Max(rowPixels, _visibleRows * rowPixels - 1);
        // Always derive from the baseline, never from a previously rounded/capped viewport.
        RowList.Height = viewportPixels / targetScale;
        return _preferredHeight - _visibleRows * RowHeight + RowList.Height;
    }

    private void BoundViewportToHeight(double height)
    {
        // Reserve measured chrome independently of the target DPI's rounded row height.
        var chromeHeight = Math.Max(0, _preferredHeight - _visibleRows * RowHeight);
        RowList.MaxHeight = Math.Min(MaxVisibleRows * RowHeight, Math.Max(0, height - chromeHeight));
    }

    /// <summary>
    /// Renders the window once at a disappearance-free position, so the later reveal at the
    /// pointer is a move rather than a first paint (measured: 69 ms of layout+render on the hover
    /// path).
    /// <para>
    /// Deliberately NOT part of <see cref="PrepareAsync"/>: this blocks the UI thread for a few
    /// hundred milliseconds, and doing it during the hold delayed the button-up long enough that
    /// the gesture was reset before it could fire. The coordinator calls it once the bar is on
    /// screen, which is while the pointer is travelling towards it.
    /// </para>
    /// </summary>
    /// <param name="at">
    /// A screen point on the monitor the list will be shown on. The window is parked inside that
    /// monitor's work area, cloaked, so WPF resolves the same DPI it will use when shown; parking
    /// it at a fixed -32000,-32000 put it nearest a different monitor and made WPF build a
    /// double-sized hand cursor for the 200% screen that was then displayed at 125%.
    /// </param>
    public void WarmRender(ScreenPoint at)
    {
        // Never touch a window that is already on screen.
        if (_rowsLoaded == false)
        {
            return;
        }

        RenderOffscreen(at);
    }

    /// <summary>
    /// Shows the window cloaked inside its target monitor, lays it out, and uncloaks nothing: it
    /// stays cloaked until the reveal, so the visual tree is realized while nothing is visible.
    /// </summary>
    private void RenderOffscreen(ScreenPoint at)
    {
        if (_warmRendered)
        {
            return;
        }

        _warmRendered = true;
        try
        {
            var nativePoint = new NativeMethods.Point(at.X, at.Y);
            var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
            var work = WorkArea(monitor);
            FitToWorkArea(work.Right - work.Left, work.Bottom - work.Top, NativeMethods.MonitorScale(monitor));

            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                work.Left,
                work.Top,
                0,
                0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

            ShowActivated = false;
            NativeMethods.SetWindowCloaked(hwnd, true);
            Show();
            UpdateLayout();

            // The rows and the scroll host exist now, so the viewport can be trimmed to a whole
            // row count. Doing it while cloaked means the resize is never seen.
            if (_visibleRows > 0)
            {
                FitViewportToRows();
                UpdateLayout();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Warm-render the compact list", ex);
            _warmRendered = false;
        }
    }

    private bool _warmRendered;

    private async Task ShowCoreAsync(
        IntPtr target,
        Window? anchor,
        bool noActivate,
        IReadOnlyList<ClipItem>? preloaded = null)
    {
        _target = target;
        _noActivate = noActivate;

        // The window may have been built by the startup prewarm, which ran before this call chose
        // the activation mode: ApplyToolWindowStyle read _noActivate=false back then and the
        // WS_EX_NOACTIVATE bit is missing. Re-apply now that the flag is known — a click on the
        // list would otherwise activate it, stripping the target app's in-place rename edit of
        // focus and committing the rename before the paste could fill it.
        if (noActivate)
        {
            ApplyToolWindowStyle();
        }

        // Prepared while the user was still holding (the common path); otherwise build now.
        await PrepareAsync(target, preloaded).ConfigureAwait(true);

        if (anchor is null)
        {
            PlaceNearCursor();
        }
        else
        {
            PlaceBelow(anchor);
        }

        // Always true today (both paths pass noActivate: true); a floating list must never take
        // the foreground on its own — see ShowBelowAsync's comment for the rename case.
        if (_noActivate)
        {
            ShowActivated = false;
        }

        // Cloak until the first rendered frame: without it the list appeared as an empty surface
        // for a beat while WPF painted the rows.
        // Positioned BEFORE revealing: moving afterwards lets WPF queue a frame at the park
        // position, which is a one-frame jump.
        ApplyPendingPlacement();

        if (_warmRendered)
        {
            // Already shown and laid out, just invisible: uncloaking is the whole reveal, and it
            // cannot flash an unpainted frame because the content has been rendered once.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowCloaked(hwnd, false);
            }
        }
        else
        {
            // Cold path (a hover that beat the warm-up): cloak until the first rendered frame.
            FlashFreeShow.Prepare(this);
            Show();

            // Laid out for the first time here, so this is the first point the viewport can be
            // measured and trimmed. Still cloaked, so the resize cannot be seen.
            if (_visibleRows > 0)
            {
                FitViewportToRows();
                UpdateLayout();
            }
        }

        // Activation is deferred to the next dispatcher turn: it costs ~25 ms and doing it inline
        // delayed the first painted frame, which is the hitch. Deferred, the list appears at once
        // and takes focus a moment later (Esc and the outside-click handling need it).
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_closing || !IsVisible)
                {
                    return;
                }

                // The coordinator-driven list (hover-expand) must NOT activate: the hover fired
                // while the user was doing something else, and taking the foreground cancels an
                // in-place rename (Explorer's F2) the moment its window deactivates. Esc still
                // works through the window's own key handling, and a row click pastes because
                // PasteService re-activates the captured target itself (it attaches to the
                // foreground thread's input queue, so the shell's lock does not block it).
                if (!_noActivate && !_managedByCoordinator)
                {
                    Activate();
                    if (KeyboardSelectionEnabled && RowList.Items.Count > 0)
                    {
                        RowList.SelectedIndex = 0;
                        RowList.Focus();
                    }
                }
            }),
            DispatcherPriority.Background);

        if (!_managedByCoordinator)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _outsideClick = OutsideClickHook.StartForPopup(
                () => !_closing && !_pasting && IsVisible,
                (x, y) => IsPointInside(hwnd, x, y),
                () =>
                {
                    if (!_closing && !_pasting)
                    {
                        Close();
                    }
                });
        }
    }

    /// <summary>
    /// Places the list directly under the floating bar. Everything here is in physical pixels:
    /// WPF converts Left/Top to device pixels with the DPI of the monitor the window is on, and a
    /// window that has never been shown is treated as being on the primary monitor. On a mixed-DPI
    /// desktop that put the list at the wrong offset (measured on a 100% primary with a 150%
    /// secondary), so it did not sit under the bar and the bar itself sat away from the pointer.
    /// </summary>
    private void PlaceBelow(Window anchor)
    {
        MeasurePreferredSize();

        var anchorHwnd = new WindowInteropHelper(anchor).Handle;
        if (anchorHwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(anchorHwnd, out var anchorRect))
        {
            PlaceNearCursor();
            return;
        }

        var monitor = NativeMethods.MonitorFromWindow(anchorHwnd, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor);

        var fitted = FitToWorkArea(work.Right - work.Left, work.Bottom - work.Top, scale);
        var width = (int)Math.Round(fitted.Width * scale);
        var height = (int)Math.Round(fitted.Height * scale);
        var gap = (int)Math.Round(6 * scale);
        var marginX = Math.Min((int)Math.Round(ScreenMargin * scale), Math.Max(0, (work.Right - work.Left - width) / 2));
        var marginY = Math.Min((int)Math.Round(ScreenMargin * scale), Math.Max(0, (work.Bottom - work.Top - height) / 2));

        var left = anchorRect.Left;
        var top = anchorRect.Bottom + gap;

        if (top + height > work.Bottom - marginY)
        {
            // Flip above the bar when there is no room below.
            top = anchorRect.Top - height - gap;
        }

        left = Math.Clamp(left, work.Left + marginX, Math.Max(work.Left + marginX, work.Right - width - marginX));
        top = Math.Clamp(top, work.Top + marginY, Math.Max(work.Top + marginY, work.Bottom - height - marginY));

        _pendingPlacement = (left, top, width, height);
    }

    /// <summary>
    /// Physical placement waiting for the handle to exist. Dimensions are used only to clamp
    /// the position; WPF applies the fitted DIP size, and the native move must use SWP_NOSIZE.
    /// </summary>
    private (int Left, int Top, int Width, int Height)? _pendingPlacement;

    private void ApplyPendingPlacement()
    {
        if (_pendingPlacement is not { } placement)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Position only: the size stays WPF's business, or the DPI change triggered by the move
        // scales it a second time (measured: a 300x447 DIP list became 600x894 physical).
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            placement.Left,
            placement.Top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>The last non-self window this app pasted into, for targets like a launcher bar.</summary>
    private static IntPtr _lastKnownTarget;

    /// <summary>Records the target so a later list can fall back to it.</summary>
    public static void RememberTarget(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _lastKnownTarget = hwnd;
        }
    }

    /// <summary>One row of the compact list. Plain data: the ListBox turns these into visuals.</summary>
    private sealed class CompactRow(ClipItem item, bool isFirst)
    {
        public ClipItem Item { get; } = item;

        /// <summary>True for the top row, whose separator is skipped: nothing precedes it.</summary>
        public bool IsFirst { get; } = isFirst;

        public string Title { get; } = TitleOf(item);

        public SymbolRegular Glyph { get; } = item.Kind == ClipKind.Files
            ? SymbolRegular.Document24
            : SymbolRegular.TextBulletListSquare24;

        /// <summary>True when this row has a decoded thumbnail instead of the glyph.</summary>
        public bool HasThumbnail { get; } = item.Kind == ClipKind.Image
            && item.ImagePath is { } path
            && File.Exists(path);

        /// <summary>The 56px thumbnail, or null. Decoded once, on first binding.</summary>
        public ImageSource? Thumbnail { get; } = item.Kind == ClipKind.Image
            && item.ImagePath is { } imagePath
            && File.Exists(imagePath)
                ? TryLoadThumbnail(imagePath)
                : null;
    }

    private async void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CompactRow row } element)
        {
            return;
        }

        // A Button captures the mouse on press and raises Click on release wherever the pointer
        // is, so pressing a row, dragging off it and letting go used to paste that row. Releasing
        // away from the row is a cancel, not a choice.
        if (!PointerInside.Element(element))
        {
            AppLog.Write("Compact list: click cancelled (released away from the row)");
            return;
        }

        await PasteAsync(row.Item).ConfigureAwait(true);
    }

    private static string TitleOf(ClipItem item) => item.Kind switch
    {
        ClipKind.Image => "图片 " + item.ImageWidth + "×" + item.ImageHeight,
        _ => FirstLine(item.Text) is { Length: > 0 } line ? line : "(空)",
    };


    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var newline = text.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? text : text[..newline];
        return line.Trim();
    }

    private static BitmapSource? TryLoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 56;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task PasteAsync(ClipItem item)
    {
        if (_pasting || _closing)
        {
            return;
        }

        _pasting = true;
        _pasteWatch.Restart();

        // The row click is still mid-dispatch: the mouse-up that triggered it happened over this
        // window. Hiding NOW makes the OS re-hit-test the point under the released button and
        // deliver a fresh click to whatever is beneath — the app being pasted into. A click into
        // an in-place rename dismisses it, so the Ctrl+V that follows has no edit to land in and
        // the paste reads as "did nothing". Keep the window visible but transparent during the
        // fade so no re-hit-test happens; hide it only after the animation finishes.
        AnimateHide();
        await Task.Delay(HideFadeMs).ConfigureAwait(true);
        Hide();
        AppLog.Write($"Compact list: hidden at {_pasteWatch.ElapsedMilliseconds} ms");
        try
        {
            // The gesture began in ClipBar's own search box: insert the text there directly.
            // Sending Ctrl+V to our own process is refused by the self-paste guard, and there is
            // no window to activate — the panel is already the foreground.
            if (_insertIntoOwnTextInput is { } insert)
            {
                var inserted = insert(item);
                AppLog.Write($"Compact list: inserted into own text input = {inserted}");
                return;
            }

            var target = _target;
            if (target == IntPtr.Zero || !PasteTarget.WorthCapturing(target, SelfProcessId, PidOf))
            {
                RememberTarget(target);
            }

            var outcome = await _pasteService
                .PasteAsync(
                    item, target, paste: true,
                    new WindowInteropHelper(this).Handle, SelfProcessId,
                    restoreClipboardAfterPaste: true)
                .ConfigureAwait(true);
            AppLog.Write($"Compact list: paste outcome {outcome} (click->outcome {_pasteWatch.ElapsedMilliseconds} ms)");
            if (outcome == PasteOutcome.CopyFailed)
            {
                AppLog.Write("Compact paste: clipboard write failed");
            }
            else if (outcome == PasteOutcome.Cancelled)
            {
                FluentMessageWindow.Toast(
                    "剪贴板或目标窗口在粘贴前发生变化，已取消自动粘贴。",
                    SymbolRegular.Warning24,
                    "粘贴已取消");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compact paste", ex);
        }
        finally
        {
            _pasting = false;
            Close();
        }
    }

    private static uint SelfProcessId => (uint)Environment.ProcessId;

    private static uint PidOf(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static bool IsPointInside(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    /// <summary>Places the list next to the pointer, clamped to the monitor's work area.</summary>
    private void PlaceNearCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        // Measure after the rows exist so the placement uses the preferred content height.
        MeasurePreferredSize();

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor);

        var fitted = FitToWorkArea(work.Right - work.Left, work.Bottom - work.Top, scale);
        var width = (int)Math.Round(fitted.Width * scale);
        var height = (int)Math.Round(fitted.Height * scale);
        var marginX = Math.Min((int)Math.Round(ScreenMargin * scale), Math.Max(0, (work.Right - work.Left - width) / 2));
        var marginY = Math.Min((int)Math.Round(ScreenMargin * scale), Math.Max(0, (work.Bottom - work.Top - height) / 2));

        var left = Math.Clamp(cursor.X, work.Left + marginX, Math.Max(work.Left + marginX, work.Right - width - marginX));
        var top = Math.Clamp(cursor.Y, work.Top + marginY, Math.Max(work.Top + marginY, work.Bottom - height - marginY));

        _pendingPlacement = (left, top, width, height);
    }

    /// <summary>The monitor's work area in physical pixels, matching GetCursorPos and SetWindowPos.</summary>
    private static NativeMethods.Rect WorkArea(IntPtr monitor)
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            var scale = NativeMethods.MonitorScale(monitor);
            return new NativeMethods.Rect
            {
                Left = (int)Math.Round(fallback.Left * scale),
                Top = (int)Math.Round(fallback.Top * scale),
                Right = (int)Math.Round(fallback.Right * scale),
                Bottom = (int)Math.Round(fallback.Bottom * scale),
            };
        }

        return info.Work;
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var extra = (long)NativeMethods.WsExToolWindow;
        if (_noActivate)
        {
            extra |= NativeMethods.WsExNoActivate;
            ShowActivated = false;
        }

        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style | extra));

        NativeMethods.SetNonClientRendering(hwnd, enabled: false);
        NativeMethods.SetRoundCorners(hwnd);
        FrameBorder.Pin(this);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // The coordinator owns dismissal for the floating pair (bar + list): it closes them when
        // the pointer leaves both, so a transient deactivation must not close just the list.
        if (_managedByCoordinator)
        {
            return;
        }

        // A window that was only prepared (handle created, never shown) must not close itself:
        // creating the handle can deliver a transient deactivation, and closing there threw away
        // the warmed-up list before the hover could use it.
        if (!IsVisible)
        {
            return;
        }

        if (!_closing && !_pasting)
        {
            AppLog.Write("Compact list: closed by deactivation");
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (KeyboardSelectionEnabled
            && e.Key is Key.Enter or Key.Space
            && RowList.SelectedItem is CompactRow row)
        {
            _ = PasteAsync(row.Item);
            e.Handled = true;
        }
    }
}
