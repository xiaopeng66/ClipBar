using System.IO;
using System.Windows.Media.Imaging;
using ClipBar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace ClipBar.App.ViewModels;

public sealed class ClipItemViewModel : ObservableObject
{
    private ClipItem _model;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _groupLabel = string.Empty;
    private SymbolRegular _iconSymbol;
    private bool _isPinned;
    private long _updatedAt;
    private bool _hasThumbnail;
    private BitmapImage? _thumbnail;
    private bool _thumbnailLoaded;

    /// <summary>
    /// The image path whose decode already failed. Panel shows re-apply the model on every row
    /// (refresh diff → Update → ApplyModel), and File.Exists stays true for a corrupt file —
    /// without this memory the fallback was reset and the row flipped back to a blank preview
    /// on the next show (probe-measured, G-04).
    /// </summary>
    private string? _decodeFailedPath;
    private string _imagePreviewMode = "Large";
    private bool _showPreview;
    private bool _showTextRow;
    private bool _showIconImage;
    private bool _showIconSymbol;

    public ClipItemViewModel(ClipItem model)
    {
        _model = model;
        ApplyModel();
        RefreshLayout();
    }

    public ClipItem Model => _model;

    public string Id => _model.Id;

    public ClipKind Kind => _model.Kind;

    private bool _isMenuAnchor;

    /// <summary>
    /// True while this row's context menu is open. The menu window sits at the pointer, so the
    /// row stops being hovered; freezing the hover appearance keeps the panel pixel-identical
    /// for the whole right-click gesture instead of dropping the highlight under the cursor.
    /// </summary>
    public bool IsMenuAnchor
    {
        get => _isMenuAnchor;
        set => SetProperty(ref _isMenuAnchor, value);
    }

    /// <summary>
    /// Observable, unlike a plain pass-through to the model: the card's pin colour is bound to
    /// it, and pinning a row now updates this instance in place instead of replacing it.
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        private set => SetProperty(ref _isPinned, value);
    }

    /// <summary>Also a sort key, so a touch (same content copied again) has to notify.</summary>
    public long UpdatedAt
    {
        get => _updatedAt;
        private set => SetProperty(ref _updatedAt, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    /// <summary>Group key for the "已固定 / 今天 / 昨天 / 更早" sections.</summary>
    public string GroupLabel
    {
        get => _groupLabel;
        private set => SetProperty(ref _groupLabel, value);
    }

    /// <summary>
    /// Whether the pointer is currently over this row, tracked by the window with explicit
    /// MouseEnter/MouseLeave handling and cleared whenever the list shifts or the panel shows.
    /// WPF's own ListBoxItem.IsMouseOver goes stale — hiding the window under a stationary
    /// mouse or shifting the rows on a new capture never delivers the leave, and the card
    /// kept its hover highlight (and the pin/delete buttons) with the mouse elsewhere.
    /// </summary>
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set => SetProperty(ref _isPointerOver, value);
    }
    private bool _isPointerOver;

    public SymbolRegular IconSymbol
    {
        get => _iconSymbol;
        private set => SetProperty(ref _iconSymbol, value);
    }

    /// <summary>
    /// True when this clip has an image file to show, independent of whether it is decoded yet.
    /// Cached: the old property called File.Exists() on every binding evaluation, and the card
    /// binds it twice — once for the preview, once for the fallback icon.
    /// </summary>
    public bool HasThumbnail
    {
        get => _hasThumbnail;
        private set
        {
            if (SetProperty(ref _hasThumbnail, value))
            {
                RefreshLayout();
            }
        }
    }

    /// <summary>True when the row renders the image preview instead of a text row.</summary>
    public bool ShowPreview => _showPreview;

    /// <summary>True when the row renders the icon slot plus title/subtitle.</summary>
    public bool ShowTextRow => _showTextRow;

    /// <summary>True when the icon slot shows the picture (small preview) rather than a symbol.</summary>
    public bool ShowIconImage => _showIconImage;

    public bool ShowIconSymbol => _showIconSymbol;

    /// <summary>
    /// Applies the image preview size chosen in 设置 (Large / Medium / Small), keeping both existing
    /// rows and newly captured clips on the same observable layout.
    /// </summary>
    public void SetImagePreviewMode(string mode)
    {
        var normalised = ClipBar.Core.Models.AppSettings.CanonicalImagePreview(mode);
        if (string.Equals(_imagePreviewMode, normalised, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _imagePreviewMode = normalised;
        RefreshLayout();
    }

    /// <summary>
    /// Pure layout decision, exposed for tests: which of the four row parts is visible.
    /// </summary>
    public static (bool Preview, bool TextRow, bool IconImage, bool IconSymbol) ComputeLayout(
        bool hasThumbnail,
        string? mode)
    {
        var small = string.Equals(mode?.Trim(), "Small", StringComparison.OrdinalIgnoreCase);
        return (
            Preview: hasThumbnail && !small,
            TextRow: !hasThumbnail || small,
            IconImage: hasThumbnail && small,
            IconSymbol: !hasThumbnail);
    }

    private void RefreshLayout()
    {
        var (preview, textRow, iconImage, iconSymbol) = ComputeLayout(_hasThumbnail, _imagePreviewMode);

        if (_showPreview != preview)
        {
            _showPreview = preview;
            OnPropertyChanged(nameof(ShowPreview));
        }

        if (_showTextRow != textRow)
        {
            _showTextRow = textRow;
            OnPropertyChanged(nameof(ShowTextRow));
        }

        if (_showIconImage != iconImage)
        {
            _showIconImage = iconImage;
            OnPropertyChanged(nameof(ShowIconImage));
        }

        if (_showIconSymbol != iconSymbol)
        {
            _showIconSymbol = iconSymbol;
            OnPropertyChanged(nameof(ShowIconSymbol));
        }
    }

    /// <summary>
    /// Decodes the image on first access and caches it. The list is virtualised, so only the
    /// cards that actually render pay for a bitmap — important now that previews are large.
    /// </summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = LoadThumbnail(_model);
                if (_thumbnail is null && _hasThumbnail)
                {
                    // The file exists but cannot be decoded (corrupt, truncated, locked): fall
                    // back to the symbol row instead of an empty preview strip. Without this the
                    // failure was cached forever and the row never recovered (G-04).
                    _decodeFailedPath = _model.ImagePath;
                    HasThumbnail = false;
                }
            }

            return _thumbnail;
        }
    }

    /// <summary>
    /// Applies a freshly queried copy of the same row in place. Returns true when a sort or
    /// group key changed, which is the only case the collection view must be told to re-order.
    /// </summary>
    public bool Update(ClipItem model)
    {
        if (!string.Equals(_model.Id, model.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a copy of the same clip can update this item.", nameof(model));
        }

        var previousPinned = _isPinned;
        var previousUpdatedAt = _updatedAt;
        var previousGroup = _groupLabel;
        var previousImagePath = _model.ImagePath;
        var previousImageWidth = _model.ImageWidth;
        var previousImageHeight = _model.ImageHeight;

        _model = model;
        ApplyModel();

        // A row can gain an image path (an image re-imported under the same hash) or be stored
        // at another resolution; only then is the already decoded bitmap stale (G-14: the height
        // counts too — same path and width at a different height used to keep the old bitmap).
        if (!string.Equals(previousImagePath, model.ImagePath, StringComparison.Ordinal)
            || previousImageWidth != model.ImageWidth
            || previousImageHeight != model.ImageHeight)
        {
            // The file was replaced or re-pointed: retry the decode, forget the failure (G-14).
            _decodeFailedPath = null;
            _thumbnail = null;
            _thumbnailLoaded = false;
            OnPropertyChanged(nameof(Thumbnail));
        }

        return previousPinned != _isPinned
            || previousUpdatedAt != _updatedAt
            || !string.Equals(previousGroup, _groupLabel, StringComparison.Ordinal);
    }

    private void ApplyModel()
    {
        IsPinned = _model.IsPinned;
        UpdatedAt = _model.UpdatedAt;
        Title = BuildTitle(_model);
        Subtitle = BuildSubtitle(_model);
        GroupLabel = BuildGroupLabel(_model);
        IconSymbol = _model.Kind switch
        {
            ClipKind.Image => SymbolRegular.Image24,
            ClipKind.Files => SymbolRegular.Folder24,
            _ => SymbolRegular.TextDescription24,
        };
        // A decode failure sticks for the path it failed on: model re-application must not
        // resurrect a row whose PNG cannot be decoded (G-04). A different path retries.
        if (!string.Equals(_decodeFailedPath, _model.ImagePath, StringComparison.Ordinal))
        {
            _decodeFailedPath = null;
        }

        HasThumbnail = _model.Kind == ClipKind.Image
            && !string.IsNullOrWhiteSpace(_model.ImagePath)
            && File.Exists(_model.ImagePath)
            && !string.Equals(_decodeFailedPath, _model.ImagePath, StringComparison.Ordinal);
    }

    private static string BuildTitle(ClipItem item)
    {
        switch (item.Kind)
        {
            case ClipKind.Image:
                return item.ImageWidth > 0 && item.ImageHeight > 0
                    ? $"图片 {item.ImageWidth}×{item.ImageHeight}"
                    : "图片";
            case ClipKind.Files:
                if (item.FilePaths.Count == 0)
                {
                    return "文件";
                }

                var first = Path.GetFileName(item.FilePaths[0]);
                return item.FilePaths.Count > 1
                    ? $"{first} 等 {item.FilePaths.Count} 个文件"
                    : first;
            default:
                var text = (item.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    return "空文本";
                }

                var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
                firstLine = firstLine.Trim();
                return firstLine.Length > 180 ? firstLine[..180] + "…" : firstLine;
        }
    }

    private static string BuildSubtitle(ClipItem item)
    {
        var source = string.IsNullOrWhiteSpace(item.SourceApp)
            ? "未知来源"
            : Path.GetFileNameWithoutExtension(item.SourceApp);

        if (source.Length > 0)
        {
            source = char.ToUpperInvariant(source[0]) + source[1..];
        }

        return $"{source} · {RelativeTime(item.UpdatedAt)}";
    }

    private static string RelativeTime(long unixMilliseconds)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime;
        var now = DateTime.Now;
        var delta = now - time;

        if (delta.TotalSeconds < 45)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (time.Date == now.Date)
        {
            return time.ToString("HH:mm");
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return "昨天 " + time.ToString("HH:mm");
        }

        // Cross-year entries need the year: "12月30日" alone is ambiguous in January (D-G-04).
        return time.Year == now.Year
            ? time.ToString("M月d日")
            : time.ToString("yyyy年M月d日");
    }

    private static string BuildGroupLabel(ClipItem item)
    {
        if (item.IsPinned)
        {
            return "已固定";
        }

        var time = DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAt).LocalDateTime;
        var today = DateTime.Today;
        if (time.Date == today)
        {
            return "今天";
        }

        return time.Date == today.AddDays(-1) ? "昨天" : "更早";
    }

    private static BitmapImage? LoadThumbnail(ClipItem item)
    {
        if (item.Kind != ClipKind.Image || string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // WPF caches decoded frames per URI: a file replaced at the same path (a clip
            // re-stored at another resolution) used to keep serving the old pixels even after
            // the cache reset below (G-14).
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

            // Previews render near their original size, so decode at the source resolution up to
            // a sane cap. Measured: DecodePixelWidth SCALES A SMALLER SOURCE UP (32×24 →
            // 1600×1200), so the cap only goes on sources that need it (G-05): a known oversized
            // image, or an unknown width (0, written by older versions) where a huge source would
            // otherwise decode at full size on the UI thread.
            if (item.ImageWidth > 1600 || item.ImageWidth == 0)
            {
                image.DecodePixelWidth = 1600;
            }

            image.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
