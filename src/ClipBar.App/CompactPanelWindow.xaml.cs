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
        Closed += (_, _) => _closing = true;
    }

    /// <summary>Fills the list and shows the popup next to the cursor.</summary>
    public async Task ShowAtCursorAsync()
    {
        // Capture the paste target BEFORE we are shown: once this window is up it owns the
        // foreground, and the clip must land in the app SnipDo was invoked from.
        var foreground = NativeMethods.GetForegroundWindow();
        _target = foreground;
        if (!PasteTarget.WorthCapturing(foreground, SelfProcessId, PidOf))
        {
            // The SnipDo toolbar itself is often the foreground; fall back to the previous
            // window of this app's own tracking when that happens.
            _target = _lastKnownTarget;
        }

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

        PlaceNearCursor();
        Show();
        Activate();
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
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style | NativeMethods.WsExToolWindow));

        NativeMethods.SetNonClientRendering(hwnd, enabled: false);
        NativeMethods.SetRoundCorners(hwnd);
        FrameBorder.Pin(this);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
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
