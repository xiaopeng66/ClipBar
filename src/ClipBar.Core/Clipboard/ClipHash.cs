using System.Security.Cryptography;
using System.Text;
using ClipBar.Core.Models;

namespace ClipBar.Core.Clipboard;

public static class ClipHash
{
    public static string Compute(ClipItem item)
    {
        var payload = new StringBuilder();
        payload.Append((int)item.Kind).Append('\n');

        switch (item.Kind)
        {
            case ClipKind.Text:
                payload.Append(item.Text ?? string.Empty).Append('\n').Append(item.Html ?? string.Empty);
                break;
            case ClipKind.Files:
                foreach (var path in item.FilePaths.Select(NormalizePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    payload.Append(path).Append('\n');
                }
                break;
            case ClipKind.Image:
                payload.Append(item.ImagePath ?? string.Empty);
                break;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ComputeImage(ReadOnlySpan<byte> imageBytes)
    {
        var bytes = SHA256.HashData(imageBytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
