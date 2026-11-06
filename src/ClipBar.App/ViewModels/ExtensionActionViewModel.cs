using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.App.Services;
using ClipBar.Core.Models;
using ClipBar.Core.Security;

namespace ClipBar.App.ViewModels;

/// <summary>A SnipDo action offered for the currently selected clip.</summary>
public sealed class ExtensionActionViewModel
{
    /// <summary>Logical size of the action button icon (the panel renders it at 16 DIP).</summary>
    private const double IconSize = 16d;

    public ExtensionActionViewModel(LoadedExtension owner, SnipDoAction action)
    {
        Owner = owner;
        Action = action;
        IconSource = LoadIcon(owner, action);
    }

    public LoadedExtension Owner { get; }

    public SnipDoAction Action { get; }

    public string Title => string.IsNullOrWhiteSpace(Action.DisplayTitle) ? "未命名动作" : Action.DisplayTitle;

    public ImageSource? IconSource { get; }

    private static ImageSource? LoadIcon(LoadedExtension owner, SnipDoAction action)
    {
        foreach (var candidate in new[]
        {
            action.IconColored,
            action.Icon,
            owner.Extension.IconColored,
            owner.Extension.Icon,
        })
        {
            var path = ResolveIcon(owner.Extension.RootPath, candidate);
            if (path is null)
            {
                continue;
            }

            // SnipDo ships its icons as SVG, which BitmapImage cannot decode.
            if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var vector = SvgIconLoader.Load(path, IconSize);
                    if (vector is not null)
                    {
                        return vector;
                    }
                }
                catch (Exception)
                {
                    // F-03 defence in depth: a malformed icon must never break action-menu
                    // construction; ParseColor is total, this backstops any other parse path.
                }

                continue;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = (int)(IconSize * 2);
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // Unreadable or unsupported bitmap: fall back to the generic symbol icon.
            }
        }

        return null;
    }

    private static string? ResolveIcon(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var trimmed = relative.Trim();
        if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // F-11 (legacy E-12): the manifest is untrusted input, so an icon may only resolve to
        // a file inside the package — the same containment rule the manifest parser applies.
        // The old consumer-side resolver accepted "../outside.svg" and absolute paths, which
        // read files from anywhere on disk.
        var path = SafePath.TryResolveInside(root, trimmed, "动作图标");
        return path is not null && File.Exists(path) ? path : null;
    }
}
