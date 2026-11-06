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
/// Fluent popup used as the tray/clip context menu: a solid-surface window that honours the
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
        // Menus that mix icon and icon-less rows (e.g. the source menu: check-marked current
        // item + plain items) read aligned only if every label starts at the same x, so
        // icon-less rows reserve the same 24 DIP icon slot.
        var anyIcon = entries.Any(e => e.Icon is not null);
        if (width <= 0)
        {
            // Content-sized rows (the clip context menu): bounded so a long community action
            // title cannot stretch the popup across the panel. The width stays unset; ShowAt
            // measures the content and assigns both dimensions before the first frame.
            window.Width = double.NaN;
            window.MinWidth = 176;
            window.MaxWidth = 300;
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

                if (anyIcon)
                {
                    // Same 16 DIP icon slot + 8 DIP gap the icon rows use.
                    var spacer = new System.Windows.Controls.Border { Width = 24 };
                    var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    rowPanel.Children.Add(spacer);
                    rowPanel.Children.Add(label);
                    content = rowPanel;
                }
                else
                {
                    content = label;
                }
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
            // Icon rows carry a StackPanel, whose content does not propagate a UIA name — screen
            // readers (and every UIA client) saw an empty row. Name it explicitly (attached
            // properties cannot be set in an object initializer).
            System.Windows.Automation.AutomationProperties.SetName(item, entry.Header);

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
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(cursorX, cursorY),
            NativeMethods.MonitorDefaultToNearest);

        // Cursor and work area are physical pixels; WPF places windows in DIPs.
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkAreaFor(monitor, scale);
        var anchorX = cursorX / scale;
        var anchorY = cursorY / scale;

        // Measure the content and assign the final size *before* the first frame. Sizing after
        // Show() painted one too-tall frame (FluentWindow's template reserves a caption strip)
        // and then shrank it, which the user saw as the menu flickering in place.
        if (Content is FrameworkElement root)
        {
            var autoWidth = double.IsNaN(Width);
            root.Measure(new Size(autoWidth ? MaxWidth : Width, double.PositiveInfinity));
            var desired = root.DesiredSize;

            if (autoWidth)
            {
                Width = Math.Clamp(Math.Ceiling(desired.Width), MinWidth, MaxWidth);
                root.Measure(new Size(Width, double.PositiveInfinity));
                desired = root.DesiredSize;
            }

            var measuredHeight = Math.Ceiling(desired.Height);
            if (measuredHeight > 0)
            {
                Height = measuredHeight;
            }
        }

        var width = double.IsNaN(Width) ? 176 : Width;
        var height = double.IsNaN(Height) ? 220 : Height;

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

        // Position first, then show: the menu must never be visible at a provisional size or
        // place, otherwise the move/resize itself is the flicker.
        Left = left;
        Top = top;
        Show();
        UpdateLayout();

        Topmost = true;

        // Activation must be verified, not assumed: the right-button-up that follows this call
        // can hand focus back to the owner, leaving the menu visible but inactive (its DWM
        // shadow fades and it ignores keys). One retry covers the race; a second failure closes
        // the menu instead of parking a dead window on screen.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!ForceForeground())
        {
            ForceForeground();
        }

        _interactive = true;
        var activated = NativeMethods.GetForegroundWindow() == hwnd;
        AppLog.Write($"Menu shown: activated={activated}, rect={Left:0},{Top:0} {Width:0}x{Height:0}");
        if (!activated)
        {
            AppLog.Write("Menu shown but activation failed; closing instead of leaving it inert");
            Close();
            return;
        }

        // Keyboard focus: a menu that is foreground but has no focused row reads as inert for
        // menu-key / Shift+F10 users. The focused row lights up via IsKeyboardFocused.
        if (MenuPanel.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault(b => b.IsEnabled) is { } first)
        {
            first.Focus();
        }
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

    /// <summary>
    /// Brings the menu to the foreground; the return value says whether it actually got there
    /// (checked against the real foreground window, not assumed from the call not throwing).
    /// </summary>
    private bool ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
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
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// True when the menu closed because it lost focus (click outside, app switch). The panel's
    /// owner-activation hand-back makes the panel active again even in that case, so the panel
    /// cannot decide from IsActive alone whether the user is still working in it.
    /// </summary>
    public bool ClosedByDeactivation { get; private set; }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        AppLog.Write($"Menu deactivated: interactive={_interactive}, closing={_closing}");
        if (_interactive && !_closing)
        {
            ClosedByDeactivation = true;
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && !_closing)
        {
            AppLog.Write("Menu closed by Esc");
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
