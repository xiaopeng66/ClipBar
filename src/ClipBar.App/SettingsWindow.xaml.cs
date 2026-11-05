using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipBar.App.Services;
using ClipBar.Core.Extensions;
using ClipBar.Core.Models;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClipBar.App;

public partial class SettingsWindow : FluentWindow
{
    private readonly Func<AppSettings, (bool Success, string Message)> _save;
    private readonly Action _clear;
    private readonly Action _openData;
    private readonly ExtensionService _extensions;
    private readonly SnipDoPluginExporter _exporter = new();
    private readonly ObservableCollection<ExtensionRow> _extensionRows = [];

    /// <summary>
    /// The settings as they are currently live in the app. The 应用 button is filled exactly while
    /// the form differs from this, so it doubles as the "you have unapplied edits" indicator.
    /// </summary>
    private AppSettings _applied;

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings, (bool Success, string Message)> save,
        Action clear,
        Action openData,
        ExtensionService extensions)
    {
        InitializeComponent();
        ThemeService.RestoreBackdropsWhenLoaded(this);
        _save = save;
        _clear = clear;
        _openData = openData;
        _extensions = extensions;

        var copy = settings.Clone();
        HotkeyBox.Text = copy.Hotkey;
        MaxItemsBox.Text = copy.MaxItems.ToString();
        ThemeBox.SelectedValue = copy.Theme;
        ImagePreviewBox.SelectedValue = copy.ImagePreview;
        PanelPositionBox.SelectedValue = copy.PanelPositionMode;
        PasteOnClickCheck.IsChecked = copy.PasteOnClick;
        KeepPanelOpenCheck.IsChecked = copy.KeepPanelOpenAfterPaste;
        StartWithWindowsCheck.IsChecked = copy.StartWithWindows;
        CaptureImagesCheck.IsChecked = copy.CaptureImages;
        CaptureFilesCheck.IsChecked = copy.CaptureFiles;
        CaptureHtmlCheck.IsChecked = copy.CaptureHtml;
        ExcludedAppsBox.Text = string.Join(Environment.NewLine, copy.ExcludedApps);

        ExtensionList.ItemsSource = _extensionRows;
        RefreshExtensions();
        RefreshPluginStatus();

        // Seeded after the form is filled and before the handlers are attached, so the initial
        // population does not count as an edit.
        _applied = Normalize(copy);
        WatchForEdits();
        UpdateApplyButton();
    }

    /// <summary>
    /// Marks the 应用 button as dirty on any edit. Wired in code rather than XAML because the
    /// constructor fills the controls first; the initial population must not count as an edit.
    /// </summary>
    private void WatchForEdits()
    {
        HotkeyBox.TextChanged += OnFormEdited;
        MaxItemsBox.TextChanged += OnFormEdited;
        ExcludedAppsBox.TextChanged += OnFormEdited;
        ThemeBox.SelectionChanged += OnFormEdited;
        ImagePreviewBox.SelectionChanged += OnFormEdited;
        PanelPositionBox.SelectionChanged += OnFormEdited;
        PasteOnClickCheck.Checked += OnFormEdited;
        PasteOnClickCheck.Unchecked += OnFormEdited;
        KeepPanelOpenCheck.Checked += OnFormEdited;
        KeepPanelOpenCheck.Unchecked += OnFormEdited;
        StartWithWindowsCheck.Checked += OnFormEdited;
        StartWithWindowsCheck.Unchecked += OnFormEdited;
        CaptureImagesCheck.Checked += OnFormEdited;
        CaptureImagesCheck.Unchecked += OnFormEdited;
        CaptureFilesCheck.Checked += OnFormEdited;
        CaptureFilesCheck.Unchecked += OnFormEdited;
        CaptureHtmlCheck.Checked += OnFormEdited;
        CaptureHtmlCheck.Unchecked += OnFormEdited;
    }

    private void OnFormEdited(object sender, RoutedEventArgs e) => UpdateApplyButton();

    /// <summary>
    /// Filled accent while there are unapplied edits, plain outline otherwise — the same look as
    /// the 保存 button, so the two read as "primary action available".
    /// </summary>
    private void UpdateApplyButton()
    {
        var dirty = !SameAs(BuildCandidate(), _applied);
        ApplyButton.Appearance = dirty ? ControlAppearance.Primary : ControlAppearance.Secondary;
        // Only one accent button at a time (D-A-02): while the form holds unapplied edits the
        // immediate action is 应用; with a clean form 保存 is the primary.
        SaveButton.Appearance = dirty ? ControlAppearance.Secondary : ControlAppearance.Primary;
    }

    /// <summary>Esc cancels the window like 取消; a dirty form asks before discarding (D-C-02).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !HasUnappliedEdits)
        {
            return;
        }

        // Closing would silently discard everything not applied yet — ask first (D-C-02).
        var close = FluentMessageWindow.Confirm(
            this,
            "设置里有尚未应用的修改，关闭后会丢失。确定关闭吗？",
            primary: "关闭",
            secondary: "继续编辑");
        if (!close)
        {
            e.Cancel = true;
        }
    }

    /// <summary>True while the form holds unapplied edits. App.ShowPanel hides this window when
    /// the panel opens, and the user should not silently lose track of pending edits (G-11).</summary>
    public bool HasUnappliedEdits => !SameAs(BuildCandidate(), _applied);

    private void RefreshExtensions()
    {
        _extensions.Reload();
        _extensionRows.Clear();
        foreach (var loaded in _extensions.Extensions)
        {
            var extension = loaded.Extension;
            _extensionRows.Add(new ExtensionRow(
                extension.Identifier,
                extension.DisplayName,
                $"{extension.Identifier} · {extension.Actions.Count} 个动作"));
        }

        var count = _extensionRows.Count;
        ExtensionSummary.Text = count == 0 ? "尚未安装扩展" : $"已安装 {count} 个扩展";
        ExtensionEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtensionList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryApply())
        {
            Close();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        TryApply();
    }

    /// <summary>
    /// Validates the form and pushes it to the app. Returns false when the window must stay
    /// open; the reason is shown above the button row instead of in a modal popup.
    /// </summary>
    private bool TryApply()
    {
        if (!int.TryParse(MaxItemsBox.Text.Trim(), out var maxItems) || maxItems is < 10 or > 5000)
        {
            ShowStatus("最多保存条数需要是 10–5000 之间的整数。");
            return false;
        }

        var candidate = Normalize(BuildCandidate()!);

        var result = _save(candidate);
        if (!result.Success)
        {
            ShowStatus(result.Message);
            return false;
        }

        // The edits are live now, so the button goes back to its plain outline look. Success needs
        // no message of its own — the button reverting is the feedback.
        _applied = candidate;
        HideStatus();
        UpdateApplyButton();
        return true;
    }

    /// <summary>
    /// Reads the form into an <see cref="AppSettings"/> without validating. Returns null only when
    /// the item count cannot be parsed, which the dirty check treats as "modified".
    /// </summary>
    private AppSettings? BuildCandidate()
    {
        if (!int.TryParse(MaxItemsBox.Text.Trim(), out var maxItems))
        {
            return null;
        }

        return new AppSettings
        {
            Hotkey = HotkeyBox.Text.Trim(),
            MaxItems = maxItems,
            Theme = ThemeBox.SelectedValue as string ?? "System",
            ImagePreview = ImagePreviewBox.SelectedValue as string ?? "Large",
            PanelPositionMode = PanelPositionBox.SelectedValue as string ?? "FollowCursor",
            HasPanelPosition = _applied.HasPanelPosition,
            PanelLeft = _applied.PanelLeft,
            PanelTop = _applied.PanelTop,
            PanelWidth = _applied.PanelWidth,
            PanelHeight = _applied.PanelHeight,
            PasteOnClick = PasteOnClickCheck.IsChecked == true,
            KeepPanelOpenAfterPaste = KeepPanelOpenCheck.IsChecked == true,
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            CaptureImages = CaptureImagesCheck.IsChecked == true,
            CaptureFiles = CaptureFilesCheck.IsChecked == true,
            CaptureHtml = CaptureHtmlCheck.IsChecked == true,
            ExcludedApps = ExcludedAppsBox.Text
                .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        };
    }

    /// <summary>
    /// Applies the same clean-up App.SaveSettings does (clamp, trim, drop blanks, de-duplicate), so
    /// a form that differs only by stray whitespace or a repeated app name does not look dirty —
    /// and does not keep the 应用 button lit after a no-op apply.
    /// </summary>
    private static AppSettings Normalize(AppSettings settings) => settings.Normalize();

    /// <summary>
    /// Field-by-field comparison of the normalised values; <see cref="AppSettings"/> has no
    /// <c>Equals</c> override, and comparing by reference would always report a difference.
    /// </summary>
    private static bool SameAs(AppSettings? left, AppSettings right) =>
        left is not null && left.ContentEquals(right);

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = ThemeBrushes.Resolve(
            "SystemFillColorCriticalBrush",
            ThemeBrushes.Critical);
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void InstallToSnipDo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _exporter.InstallPasteAction();
            RefreshPluginStatus();
            FluentMessageWindow.Notify(
                this,
                "已安装到 SnipDo。\n\n" +
                "重启 SnipDo 后，在输入框长按鼠标左键，剪贴板栏里的「剪贴板」就会打开 ClipBar 的历史面板。\n\n" +
                "写入位置：" + target,
                SymbolRegular.CheckmarkCircle24,
                "安装完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("Install SnipDo plugin", ex);
            FluentMessageWindow.Notify(
                this,
                "安装失败：" + ex.Message,
                SymbolRegular.Warning24,
                "安装失败");
        }
    }

    private void ExportPlugin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plugin = _exporter.Export();
            RefreshPluginStatus();
            FluentMessageWindow.Notify(
                this,
                "已导出扩展包：\n" + plugin.PackagePath +
                "\n\n在 SnipDo 里用「安装扩展 / Import extension」选中这个 .pbar 即可。",
                SymbolRegular.CheckmarkCircle24,
                "导出完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Export SnipDo plugin", ex);
            FluentMessageWindow.Notify(this, "导出失败：" + ex.Message, SymbolRegular.Warning24, "导出失败");
        }
    }

    private void OpenPluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_exporter.OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _exporter.OutputDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open plugin folder", ex);
        }
    }

    /// <summary>Reports whether SnipDo's paste-action store already carries our action.</summary>
    private void RefreshPluginStatus()
    {
        PluginStatus.Text = SnipDoPluginExporter.IsPasteActionInstalled()
            ? "已安装到 SnipDo。改动快捷键或路径后重新点「安装到 SnipDo」刷新脚本，然后重启 SnipDo。"
            : "尚未安装。点「安装到 SnipDo」会写入 SnipDo 的剪贴板栏；也可以先「导出 .pbar」再在 SnipDo 里导入。";
    }

    private async void ImportExtension_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SnipDo 扩展包",
            Filter = "SnipDo 扩展 (*.pbar;*.zip)|*.pbar;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var loaded = await _extensions.ImportAsync(dialog.FileName);
            RefreshExtensions();
            FluentMessageWindow.Notify(
                this,
                $"已导入「{loaded.Extension.DisplayName}」，包含 {loaded.Extension.Actions.Count} 个动作。",
                SymbolRegular.CheckmarkCircle24,
                "导入完成");
        }
        catch (Exception ex)
        {
            // Not a narrowed filter: the import touches the user's disk (create the staging dir,
            // copy entries, delete the staging dir on failure), so UnauthorizedAccessException and
            // ArgumentException escape any list of "expected" types — and this handler is async
            // void, so an escape means the user sees no message at all, only a log line. Every
            // failure here deserves the same sentence, so there is nothing to narrow to.
            AppLog.Error("Import extension", ex);
            FluentMessageWindow.Notify(this, "导入失败：" + ex.Message, SymbolRegular.Warning24, "导入失败");
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 SnipDo 扩展目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var loaded = await _extensions.ImportFolderAsync(dialog.FolderName);
            RefreshExtensions();
            FluentMessageWindow.Notify(
                this,
                $"已导入「{loaded.Extension.DisplayName}」，包含 {loaded.Extension.Actions.Count} 个动作。",
                SymbolRegular.CheckmarkCircle24,
                "导入完成");
        }
        catch (Exception ex)
        {
            // Same reasoning as ImportExtension_Click above.
            AppLog.Error("Import extension folder", ex);
            FluentMessageWindow.Notify(this, "导入失败：" + ex.Message, SymbolRegular.Warning24, "导入失败");
        }
    }

    private void RemoveExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtensionRow row)
        {
            return;
        }

        if (!FluentMessageWindow.Confirm(
                this,
                $"移除扩展「{row.Name}」？",
                primary: "移除",
                secondary: "取消"))
        {
            return;
        }

        if (!_extensions.Remove(row.Identifier))
        {
            FluentMessageWindow.Notify(
                this,
                "移除失败：扩展目录可能被占用。",
                SymbolRegular.Warning24,
                "移除失败");
        }

        RefreshExtensions();
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_extensions.ExtensionsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _extensions.ExtensionsDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open extensions folder", ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Clear_Click(object sender, RoutedEventArgs e) => _clear();

    private void OpenData_Click(object sender, RoutedEventArgs e) => _openData();
}

public sealed record ExtensionRow(string Identifier, string Name, string Detail);
