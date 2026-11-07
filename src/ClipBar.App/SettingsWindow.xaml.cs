using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Models;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClipBar.App;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, (bool Success, string Message)> _save;
    private readonly Action _clear;
    private readonly Action _openData;

    /// <summary>
    /// The settings as they are currently live in the app. The 应用 button is filled exactly while
    /// the form differs from this, so it doubles as the "you have unapplied edits" indicator.
    /// </summary>
    private AppSettings _applied;

    public SettingsWindow(
        Func<AppSettings> settings,
        Func<AppSettings, (bool Success, string Message)> save,
        Action clear,
        Action openData)
    {
        InitializeComponent();
        // White-edge fix: without a pinned DWM border colour this window carries the system
        // frame's default (light, state-dependent) border — the same edge the tray menu showed
        // over the taskbar. The panel has pinned this colour since its own fix.
        FrameBorder.Pin(this);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.SetRoundCorners(hwnd);

            // The window stays resizable without a native caption: WindowChrome supplies the
            // resize border while CaptionHeight=0 keeps any title-bar hit-test area away.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
        };
        _settings = settings;
        _save = save;
        _clear = clear;
        _openData = openData;

        FillForm(settings().Clone());

        // Seeded after the form is filled and before the handlers are attached, so the initial
        // population does not count as an edit.
        _applied = Normalize(settings().Clone());
        WatchForEdits();
        UpdateApplyButton();
    }

    /// <summary>Pushes a settings snapshot onto the controls. Used by the constructor and by a
    /// confirmed discard, which must put the applied values back before the window closes.</summary>
    private void FillForm(AppSettings source)
    {
        HotkeyBox.Text = source.Hotkey;
        MaxItemsBox.Text = source.MaxItems.ToString();
        ThemeBox.SelectedValue = source.Theme;
        ImagePreviewBox.SelectedValue = source.ImagePreview;
        PanelPositionBox.SelectedValue = source.PanelPositionMode;
        PasteOnClickCheck.IsChecked = source.PasteOnClick;
        BingSearchCheck.IsChecked = source.ShowBingSearch;
        NativeFloatingCheck.IsChecked = source.NativeFloatingEnabled;
        InvokeOnDragCheck.IsChecked = source.InvokeOnDrag;
        HoldDurationBox.Text = source.HoldDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeepPanelOpenCheck.IsChecked = source.KeepPanelOpenAfterPaste;
        StartWithWindowsCheck.IsChecked = source.StartWithWindows;
        CaptureImagesCheck.IsChecked = source.CaptureImages;
        CaptureFilesCheck.IsChecked = source.CaptureFiles;
        CaptureHtmlCheck.IsChecked = source.CaptureHtml;
        ExcludedAppsBox.Text = string.Join(Environment.NewLine, source.ExcludedApps ?? []);
        HideStatus();
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
        BingSearchCheck.Checked += OnFormEdited;
        BingSearchCheck.Unchecked += OnFormEdited;
        PasteOnClickCheck.Checked += OnFormEdited;
        PasteOnClickCheck.Unchecked += OnFormEdited;
        NativeFloatingCheck.Checked += OnFormEdited;
        NativeFloatingCheck.Unchecked += OnFormEdited;
        InvokeOnDragCheck.Checked += OnFormEdited;
        InvokeOnDragCheck.Unchecked += OnFormEdited;
        HoldDurationBox.TextChanged += OnFormEdited;
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

    /// <summary>Custom title row: the FluentWindow caption is gone with the template.</summary>
    private void TitleDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
            return;
        }

        // Confirmed discard: put the applied values back on the form. The window is about to
        // close, but App.ShowSettings re-shows a parked instance as-is, so the discarded edits
        // must not survive on screen into that next open.
        FillForm(_applied);
        UpdateApplyButton();
    }

    /// <summary>True while the form holds unapplied edits. App.ShowPanel hides this window when
    /// the panel opens, and the user should not silently lose track of pending edits (G-11).</summary>
    public bool HasUnappliedEdits => !SameAs(BuildCandidate(), _applied);

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

        if (!int.TryParse(HoldDurationBox.Text.Trim(), out var holdMs) || holdMs is < 200 or > 2000)
        {
            ShowStatus("长按触发时长需要是 200–2000 之间的整数毫秒。");
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

        // Start from the LIVE settings, never a bare new AppSettings: fields the form does not
        // own (panel geometry, PanelPositionInPixels, the file Version) must round-trip exactly.
        // The old rebuild dropped PanelPositionInPixels, so a freshly opened, untouched window
        // compared false against the live true and nagged "未应用的修改" on every close.
        var candidate = _settings().Clone();

        // Geometry is not a form field, and it moves under this window while it is open (the
        // panel saves on hide). Align the baseline with the live values so a stale snapshot can
        // neither fake a dirty form nor overwrite newer geometry on save.
        _applied.HasPanelPosition = candidate.HasPanelPosition;
        _applied.PanelPositionInPixels = candidate.PanelPositionInPixels;
        _applied.PanelLeft = candidate.PanelLeft;
        _applied.PanelTop = candidate.PanelTop;
        _applied.PanelWidth = candidate.PanelWidth;
        _applied.PanelHeight = candidate.PanelHeight;

        candidate.Hotkey = HotkeyBox.Text.Trim();
        candidate.MaxItems = maxItems;
        candidate.Theme = ThemeBox.SelectedValue as string ?? "System";
        candidate.ImagePreview = ImagePreviewBox.SelectedValue as string ?? "Large";
        candidate.PanelPositionMode = PanelPositionBox.SelectedValue as string ?? "MonitorCenter";
        candidate.NativeFloatingEnabled = NativeFloatingCheck.IsChecked == true;
        candidate.InvokeOnDrag = InvokeOnDragCheck.IsChecked == true;
        if (int.TryParse(HoldDurationBox.Text.Trim(), out var holdMs))
        {
            candidate.HoldDurationMs = holdMs;
        }
        candidate.ShowBingSearch = BingSearchCheck.IsChecked == true;
        candidate.PasteOnClick = PasteOnClickCheck.IsChecked == true;
        candidate.KeepPanelOpenAfterPaste = KeepPanelOpenCheck.IsChecked == true;
        candidate.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        candidate.CaptureImages = CaptureImagesCheck.IsChecked == true;
        candidate.CaptureFiles = CaptureFilesCheck.IsChecked == true;
        candidate.CaptureHtml = CaptureHtmlCheck.IsChecked == true;
        candidate.ExcludedApps = ExcludedAppsBox.Text
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return candidate;
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Clear_Click(object sender, RoutedEventArgs e) => _clear();

    private void OpenData_Click(object sender, RoutedEventArgs e) => _openData();
}

public sealed record ExtensionRow(string Identifier, string Name, string Detail);
