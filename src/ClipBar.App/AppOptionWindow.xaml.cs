using System.Windows;
using System.Windows.Controls;
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
