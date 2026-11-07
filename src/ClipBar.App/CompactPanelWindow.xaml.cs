using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using Wpf.Ui.Controls;

namespace ClipBar.App;

/// <summary>
/// The compact clipboard list the SnipDo paste action opens: recent clips only — no search, no
/// filters, no footer actions — anchored next to the pointer. Clicking a row pastes it into the
/// window that was in front when the list appeared, which is what SnipDo's own clipboard view
/// does. It is deliberately a plain Window with its own surface (same as the other popups), so
/// a missing theme dictionary cannot paint a native caption over it.
/// </summary>
public partial class CompactPanelWindow : Window
{
    /// <summary>How many rows the compact list shows; it is a quick picker, not the full panel.</summary>
    private const int MaxRows = 12;

    private const double ScreenMargin = 6;

    private readonly IClipRepository _repository;
    private readonly PasteService _pasteService;
    private readonly Func<AppSettings> _settings;
    private IntPtr _target;
    private bool _closing;
    private bool _pasting;

    /// <summary>
    /// Set when the floating bar opens the list: the list must not activate, or the app the user
    /// was typing in loses focus mid-gesture. The coordinator then owns dismissal for both
    /// windows, so no per-window outside-click hook is installed.
    /// </summary>
    private bool _noActivate;

    private Window? _anchor;
    private bool _managedByCoordinator;

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
        // Capture the paste target BEFORE we are shown: once this window is up it owns the
        // foreground, and the clip must land in the app SnipDo was invoked from.
        var foreground = NativeMethods.GetForegroundWindow();
        var target = foreground;
        if (!PasteTarget.WorthCapturing(foreground, SelfProcessId, PidOf))
        {
            // The SnipDo toolbar itself is often the foreground; fall back to the previous
            // window of this app's own tracking when that happens.
            target = _lastKnownTarget;
        }

        await ShowCoreAsync(target, anchor: null, noActivate: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the list below the floating bar without activating (the target app keeps focus),
    /// anchored to <paramref name="anchor"/> and pasting into <paramref name="target"/>.
    /// </summary>
    public async Task ShowBelowAsync(Window anchor, IntPtr target)
    {
        _anchor = anchor;
        _managedByCoordinator = true;

        // Deliberately activated on click (ShowActivated=false still keeps the caret in the
        // target until then): a WS_EX_NOACTIVATE list did not receive WPF mouse input at all,
        // so its rows could not be clicked. PasteService re-activates the captured target
        // before sending Ctrl+V, so the click cannot paste into ClipBar itself.
        await ShowCoreAsync(target, anchor, noActivate: false).ConfigureAwait(true);
    }

    /// <summary>True when the pointer is over the list; the coordinator keeps it open for this.</summary>
    public bool IsPointerOver => IsMouseOver;

    private async Task ShowCoreAsync(IntPtr target, Window? anchor, bool noActivate)
    {
        _target = target;
        _noActivate = noActivate;

        var rows = await _repository
            .QueryAsync(ClipFilter.All, search: null, limit: MaxRows)
            .ConfigureAwait(true);

        RowPanel.Children.Clear();
        foreach (var item in rows.Take(MaxRows))
        {
            RowPanel.Children.Add(BuildRow(item));
        }

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FooterText.Text = rows.Count == 0
            ? "复制点内容再来"
            : $"{rows.Count} 条 · 点击即粘贴 · Esc 关闭";

        if (anchor is null)
        {
            PlaceNearCursor();
        }
        else
        {
            PlaceBelow(anchor);
        }

        if (_noActivate)
        {
            // Must be set before Show(): WPF reads it when the window is shown, and a floating
            // list that activated would take the caret away from the app being pasted into.
            ShowActivated = false;
        }

        Show();

        if (!_noActivate)
        {
            Activate();
        }

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

    /// <summary>Places the list directly under the floating bar, clamped to the work area.</summary>
    private void PlaceBelow(Window anchor)
    {
        var anchorPoint = new NativeMethods.Point(
            (int)Math.Round(anchor.Left * 1),
            (int)Math.Round(anchor.Top * 1));

        if (Content is FrameworkElement root)
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            var desired = Math.Ceiling(root.DesiredSize.Height);
            if (desired > 0)
            {
                Height = desired;
            }
        }

        var width = double.IsNaN(Width) ? 300 : Width;
        var height = double.IsNaN(Height) ? 260 : Height;
        var gap = 6.0;

        var left = Math.Max(anchor.Left, anchor.Left);
        var top = anchor.Top + anchor.ActualHeight + gap;

        var monitor = NativeMethods.MonitorFromPoint(anchorPoint, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor, scale);

        if (top + height > work.Bottom - ScreenMargin)
        {
            // Flip above the bar when there is no room below.
            top = Math.Max(work.Top + ScreenMargin, anchor.Top - height - gap);
        }

        Left = Math.Max(work.Left + ScreenMargin, Math.Min(left, work.Right - width - ScreenMargin));
        Top = Math.Max(work.Top + ScreenMargin, top);
    }

    /// <summary>The last non-self window this app pasted into, for targets like SnipDo's toolbar.</summary>
    private static IntPtr _lastKnownTarget;

    /// <summary>Records the target so a SnipDo-invoked list can fall back to it.</summary>
    public static void RememberTarget(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _lastKnownTarget = hwnd;
        }
    }

    private FrameworkElement BuildRow(ClipItem item)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (item.Kind == ClipKind.Image && item.ImagePath is { } imagePath && File.Exists(imagePath))
        {
            content.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            var thumb = new System.Windows.Controls.Image
            {
                Width = 28,
                Height = 28,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 8, 0),
                Source = TryLoadThumbnail(imagePath),
            };
            Grid.SetColumn(thumb, 0);
            content.Children.Add(thumb);
        }
        else
        {
            var icon = new SymbolIcon
            {
                Symbol = item.Kind == ClipKind.Files ? SymbolRegular.Document24 : SymbolRegular.TextBulletListSquare24,
                FontSize = 15,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrushes.Resolve("TextFillColorSecondaryBrush", ThemeBrushes.SecondaryText),
            };
            content.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Title(item),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = ThemeBrushes.Resolve("TextFillColorPrimaryBrush", ThemeBrushes.PrimaryText),
        });
        texts.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Subtitle(item),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = ThemeBrushes.Resolve("TextFillColorTertiaryBrush", ThemeBrushes.SecondaryText),
        });
        Grid.SetColumn(texts, 1);
        content.Children.Add(texts);

        var button = new System.Windows.Controls.Button
        {
            Style = (Style)TryFindResource("CompactRow")!,
            Content = content,
            ToolTip = Title(item),
        };
        AutomationProperties.SetName(button, Title(item));
        button.Click += async (_, _) => await PasteAsync(item).ConfigureAwait(true);
        return button;
    }

    private static string Title(ClipItem item) => item.Kind switch
    {
        ClipKind.Image => "图片 " + item.ImageWidth + "×" + item.ImageHeight,
        _ => FirstLine(item.Text) is { Length: > 0 } line ? line : "(空)",
    };

    private static string Subtitle(ClipItem item)
    {
        var source = string.IsNullOrWhiteSpace(item.SourceApp) ? "剪贴板" : item.SourceApp;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAt).ToLocalTime().ToString("HH:mm");
        return item.Kind == ClipKind.Files && item.FilePaths.Count > 0
            ? $"{item.FilePaths.Count} 个文件 · {source} · {when}"
            : $"{source} · {when}";
    }

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
        Hide();
        try
        {
            var target = _target;
            if (target == IntPtr.Zero || !PasteTarget.WorthCapturing(target, SelfProcessId, PidOf))
            {
                RememberTarget(target);
            }

            var outcome = await _pasteService
                .PasteAsync(item, target, paste: true, new WindowInteropHelper(this).Handle, SelfProcessId)
                .ConfigureAwait(true);
            AppLog.Write($"Compact list: paste outcome {outcome}");
            if (outcome == PasteOutcome.CopyFailed)
            {
                AppLog.Write("Compact paste: clipboard write failed");
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

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor, scale);

        // Measure after the rows exist so the placement uses the real height.
        if (Content is FrameworkElement root)
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            var desired = Math.Ceiling(root.DesiredSize.Height);
            if (desired > 0)
            {
                Height = desired;
            }
        }

        var height = double.IsNaN(Height) ? 260 : Height;
        var left = Math.Max(work.Left + ScreenMargin, Math.Min(cursor.X / scale, work.Right - Width - ScreenMargin));
        var top = Math.Max(work.Top + ScreenMargin, Math.Min(cursor.Y / scale, work.Bottom - height - ScreenMargin));
        Left = left;
        Top = top;
    }

    private static Rect WorkArea(IntPtr monitor, double scale)
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            return new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var work = info.Work;
        return new Rect(
            work.Left / scale,
            work.Top / scale,
            (work.Right - work.Left) / scale,
            (work.Bottom - work.Top) / scale);
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var extra = NativeMethods.WsExToolWindow;
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

        if (!_closing && !_pasting)
        {
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
