using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipBar.App.Platform;
using Wpf.Ui.Controls;

namespace ClipBar.App;

/// <summary>
/// In-app confirmation / notice dialog. Replaces the native Win32 MessageBox, whose grey
/// bevel-and-OK button chrome looks foreign next to the Fluent panel and settings window.
/// </summary>
public partial class FluentMessageWindow : FluentWindow
{
    private FluentMessageWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
    }

    /// <summary>
    /// Set for a non-modal toast (D-C-06): DialogResult may only be assigned on a modal window,
    /// so the button handlers close the window instead.
    /// </summary>
    private bool _modal = true;

    /// <summary>Yes/No style confirmation; returns true only when the primary button is pressed.</summary>
    public static bool Confirm(Window? owner, string message, string primary = "确定", string secondary = "取消")
    {
        return ShowCore(owner, message, primary, secondary, SymbolRegular.Question24, null) == true;
    }

    /// <summary>Single-button notice.</summary>
    public static void Notify(
        Window? owner,
        string message,
        SymbolRegular symbol = SymbolRegular.Info24,
        string? heading = null)
    {
        ShowCore(owner, message, "确定", null, symbol, heading);
    }

    /// <summary>
    /// Non-modal toast that closes itself after a few seconds. Used for paste failures: a modal
    /// dialog here would disable every thread window, including a panel the user re-opened while
    /// the paste was awaiting (D-C-06).
    /// </summary>
    public static void Toast(
        string message,
        SymbolRegular symbol = SymbolRegular.Info24,
        string? heading = null,
        TimeSpan? duration = null,
        string? actionLabel = null,
        Action? action = null)
    {
        var window = new FluentMessageWindow { _modal = false, Topmost = false };
        window.Glyph.Symbol = symbol;
        window.MessageText.Text = message;
        window.PrimaryButton.Content = actionLabel ?? "确定";
        if (action is not null)
        {
            // Runs after the built-in handler closed the (non-modal) window.
            window.PrimaryButton.Click += (_, _) => action();
        }
        window.SecondaryButton.Visibility = Visibility.Collapsed;
        window.Title = heading ?? "ClipBar";
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = duration ?? TimeSpan.FromSeconds(4),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        window.Loaded += (_, _) => timer.Start();
        window.Show();
    }

    private static bool? ShowCore(
        Window? owner,
        string message,
        string primary,
        string? secondary,
        SymbolRegular symbol,
        string? heading)
    {
        var window = new FluentMessageWindow();
        window.Glyph.Symbol = symbol;
        window.MessageText.Text = message;
        window.PrimaryButton.Content = primary;
        window.Title = heading ?? "ClipBar";

        if (secondary is null)
        {
            window.SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            window.SecondaryButton.Content = secondary;
        }

        // The panel hides itself on deactivation; keep the dialog owned so the panel
        // does not vanish behind it.
        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
            return window.ShowDialog();
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog();
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

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        // Assigning DialogResult closes the modal window; calling Close() as well would
        // be a redundant second close. A toast is non-modal: DialogResult is illegal there.
        if (_modal)
        {
            DialogResult = true;
        }
        else
        {
            Close();
        }
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_modal)
        {
            DialogResult = false;
        }
        else
        {
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_modal)
            {
                DialogResult = false;
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (_modal)
            {
                DialogResult = true;
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }
}
