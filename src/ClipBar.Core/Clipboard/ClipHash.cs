using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBar.Core.Models;

namespace ClipBar.Core.Clipboard;

public static class ClipHash
{
    public static string Compute(ClipItem item)
        => Hash((int)item.Kind + "\n" + ContentPayload(item));

    // JSON string arrays preserve field and file-entry boundaries, including embedded newlines.
    private static string ContentPayload(ClipItem item) => item.Kind switch
    {
        ClipKind.Text => JsonSerializer.Serialize(new[] { item.Text ?? string.Empty, item.Html ?? string.Empty }),
        ClipKind.Files => JsonSerializer.Serialize(item.FilePaths.Select(NormalizePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)),
        ClipKind.Image => item.ImagePath ?? string.Empty,
        _ => string.Empty,
    };

    internal static bool HasSameContent(ClipItem first, ClipItem second)
        => first.Kind == second.Kind
            && string.Equals(ContentPayload(first), ContentPayload(second), StringComparison.Ordinal);

    // Lookup-only compatibility for pre-existing rows. Never use this ambiguous encoding for writes.
    internal static string ComputeLegacy(ClipItem item)
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

        return Hash(payload.ToString());
    }

    private static string Hash(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    public static string ComputeImage(ReadOnlySpan<byte> imageBytes)
    {
        var bytes = SHA256.HashData(imageBytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
