using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using Wpf.Ui.Controls;

namespace ClipBar.App;

/// <summary>
/// Fluent popup used as the tray context menu: a real Acrylic window that honours the
/// monitor DPI, instead of the default WPF ContextMenu popup (flat gray, blurry at 125%+).
/// </summary>
public partial class TrayMenuWindow : FluentWindow
{
    private const double ScreenMargin = 6;

    private bool _interactive;

    // Deactivation fires *during* a close as focus leaves; calling Close() from inside that
    // notification throws "cannot call Close while the window is closing" (measured in the
    // automation harness), so every close trigger checks this flag first.
    private bool _closing;

    private TrayMenuWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Closed += (_, _) => _closing = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    /// <summary>Builds the menu from the supplied commands and shows it next to <paramref name="anchor"/>.</summary>
    /// <param name="entries">Rows to show; an entry without an action renders disabled.</param>
    /// <param name="width">Fixed window width in DIP, or 0 to size the window to its content.</param>
    public static TrayMenuWindow Create(IReadOnlyList<TrayMenuEntry> entries, double width = 176)
    {
        var window = new TrayMenuWindow();
        if (width <= 0)
        {
            // Content-sized rows (the clip context menu): bounded so a long community action
            // title cannot stretch the popup across the panel.
            window.Width = double.NaN;
            window.MinWidth = 176;
            window.MaxWidth = 300;
            window.SizeToContent = SizeToContent.WidthAndHeight;
        }

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                window.MenuPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(6, 3, 6, 3),
                    Background = ThemeBrushes.Resolve("DividerStrokeColorDefaultBrush", ThemeBrushes.Divider),
                });
                continue;
            }

            System.Windows.Controls.TextBlock label;
            object content;
            if (entry.Icon is { } icon)
            {
                label = new System.Windows.Controls.TextBlock
                {
                    Text = entry.Header,
                    FontSize = 12,
                    FontWeight = entry.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeBrushes.Resolve("TextFillColorPrimaryBrush", ThemeBrushes.PrimaryText),
                };
                if (width <= 0)
                {
                    label.MaxWidth = 260;
                    label.TextTrimming = TextTrimming.CharacterEllipsis;
                    label.TextWrapping = TextWrapping.NoWrap;
                }

                content = new StackPanel { Orientation = Orientation.Horizontal };
                ((StackPanel)content).Children.Add(icon);
                ((StackPanel)content).Children.Add(label);
            }
            else
            {
                label = new System.Windows.Controls.TextBlock
                {
                    Text = entry.Header,
                    FontSize = 12,
                    FontWeight = entry.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeBrushes.Resolve("TextFillColorPrimaryBrush", ThemeBrushes.PrimaryText),
                };
                if (width <= 0)
                {
                    label.MaxWidth = 260;
                    label.TextTrimming = TextTrimming.CharacterEllipsis;
                    label.TextWrapping = TextWrapping.NoWrap;
                }

                content = label;
            }

            var item = new System.Windows.Controls.Button
            {
                // TryFindResource instead of the indexer: a miss yields null and the button falls
                // back to the default style instead of throwing while the tray menu is opening.
                Style = window.TryFindResource("TrayMenuItem") as Style,
                Content = content,
                // An entry without an action (the "no actions fit" placeholder) is disabled.
                IsEnabled = entry.Action is not null,
            };

            var handler = entry.Action;
            item.Click += (_, _) =>
            {
                window.Close();
                handler?.Invoke();
            };
            window.MenuPanel.Children.Add(item);
        }

        return window;
    }

    /// <summary>Shows the menu anchored to the cursor, clamped to the cursor's monitor work area.</summary>
    /// <param name="cursorX">Cursor X in physical screen pixels (as returned by GetCursorPos).</param>
    /// <param name="cursorY">Cursor Y in physical screen pixels.</param>
    /// <param name="aboveCursor">Tray menus open above the cursor; a row context menu opens below-right.</param>
    public void ShowAt(int cursorX, int cursorY, bool aboveCursor = true)
    {
        // Phase 1: show on the anchor's monitor so its DPI matches before measuring. The first
        // frame is parked near the cursor, not at (0,0): origin parking made the menu flash in the
        // top-left corner of the screen before it jumped into place.
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(cursorX, cursorY),
            NativeMethods.MonitorDefaultToNearest);

        // Cursor and work area are physical pixels; WPF places windows in DIPs.
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkAreaFor(monitor, scale);
        var anchorX = cursorX / scale;
        var anchorY = cursorY / scale;

        // Park at the position the menu will most likely occupy, so the first frame never jumps:
        // a dropdown opens at the pointer, a tray menu opens above-right of it.
        Left = aboveCursor
            ? Math.Max(work.Left + ScreenMargin, anchorX - 176)
            : Math.Max(work.Left + ScreenMargin, Math.Min(anchorX, work.Right - 320));
        Top = aboveCursor
            ? Math.Max(work.Top + ScreenMargin, anchorY - 40)
            : Math.Min(anchorY + ScreenMargin, work.Bottom - 200);
        Show();
        UpdateLayout();

        var width = ActualWidth > 0 ? ActualWidth : 176;
        var height = ActualHeight > 0 ? ActualHeight : 220;

        // Prefer above-right of the icon (Win11 tray behaviour), clamped to the work area.
        double left;
        double top;
        if (aboveCursor)
        {
            left = Math.Max(
                work.Left + ScreenMargin,
                Math.Min(anchorX - width, work.Right - width - ScreenMargin));
            top = Math.Max(
                work.Top + ScreenMargin,
                Math.Min(anchorY - height - ScreenMargin, work.Bottom - height - ScreenMargin));
        }
        else
        {
            // Classic dropdown: below-right of the pointer, flipped up when the bottom would clip.
            left = Math.Max(
                work.Left + ScreenMargin,
                Math.Min(anchorX, work.Right - width - ScreenMargin));
            top = anchorY + ScreenMargin;
            if (top + height > work.Bottom - ScreenMargin)
            {
                top = Math.Max(work.Top + ScreenMargin, anchorY - height - ScreenMargin);
            }
        }

        Left = left;
        Top = top;

        Topmost = true;
        ForceForeground();

        // Activation completes after the first deactivation notification; ignore it.
        _interactive = true;
    }

    /// <summary>Monitor work area converted to DIPs with the monitor's own scale.</summary>
    private static Rect WorkAreaFor(IntPtr monitor, double scale)
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
    }

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);

        if (attached)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }

        Activate();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_interactive && !_closing)
        {
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && !_closing)
        {
            Close();
            e.Handled = true;
        }
    }
}

/// <summary>One row of the tray menu; separators carry no action, and so does a disabled placeholder.</summary>
public sealed record TrayMenuEntry(string Header, Action? Action, bool IsSeparator = false, bool Bold = false, UIElement? Icon = null)
{
    public static TrayMenuEntry Separator() => new(string.Empty, null, IsSeparator: true);

    public static TrayMenuEntry Item(string header, Action action, bool bold = false, UIElement? icon = null) =>
        new(header, action, Bold: bold, Icon: icon);
}
