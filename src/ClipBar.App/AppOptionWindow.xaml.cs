using System.Windows;
using System.Windows.Controls;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Models;
using Wpf.Ui.Controls;

namespace ClipBar.App;

/// <summary>
/// Asks for the values a SnipDo action declares under "options" before running it.
/// SnipDo shows one selector per option, labelled by the dictionary key.
/// </summary>
public partial class AppOptionWindow : FluentWindow
{
    private readonly List<(SnipDoAppOption Option, ComboBox Box)> _selectors = [];

    public AppOptionWindow(SnipDoAction action, IEnumerable<SnipDoAppOption> options)
    {
        InitializeComponent();
        ThemeService.RestoreBackdropsWhenLoaded(this);
        // White-edge fix: pin the DWM border like the panel/menus do, so the system frame's
        // default light border cannot outline this window (FrameBorder re-pins on every
        // activation transition, which repaints the frame).
        FrameBorder.Pin(this);

        // F-09: SizeToContent=Height alone let 24 selectors push the window past the work
        // area, putting later options and the 运行/取消 buttons off-screen. The selector area
        // scrolls within a cap that leaves room for the title bar, hint and button row; the
        // cap uses the real work area so it holds on any monitor size.
        OptionScroll.MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 220);

        HintText.Text = $"{action.DisplayTitle} 需要先选择一个参数：";

        foreach (var option in options)
        {
            if (option.Values.Count == 0)
            {
                // Options backed by a script are evaluated by the runner, not the user.
                continue;
            }

            var box = new ComboBox
            {
                Height = 34,
                Margin = new Thickness(0, 0, 0, 10),
                ItemsSource = option.Values.Keys.ToList(),
                SelectedIndex = 0,
            };

            var label = string.IsNullOrWhiteSpace(option.Identifier) ? "参数" : option.Identifier;
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = ThemeBrushes.Resolve("TextFillColorSecondaryBrush", ThemeBrushes.SecondaryText),
            });
            panel.Children.Add(box);

            OptionPanel.Children.Add(panel);
            _selectors.Add((option, box));
        }

        if (_selectors.Count == 0)
        {
            // Nothing to ask: close immediately and let the caller run the action.
            Loaded += (_, _) => DialogResult = true;
        }
    }

    /// <summary>Values chosen by the user, keyed by option identifier.</summary>
    public IReadOnlyDictionary<string, string> Result { get; private set; } = new Dictionary<string, string>();

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (option, box) in _selectors)
        {
            var key = box.SelectedItem as string;
            if (key is null || !option.Values.TryGetValue(key, out var value))
            {
                continue;
            }

            values[option.Identifier] = value;
        }

        Result = values;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
