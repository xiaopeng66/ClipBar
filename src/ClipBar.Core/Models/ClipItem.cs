namespace ClipBar.Core.Models;

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ClipKind Kind { get; set; }

    /// <summary>Plain text, or a searchable fallback for files/images.</summary>
    public string? Text { get; set; }

    public string? Html { get; set; }

    public string? ImagePath { get; set; }

    public List<string> FilePaths { get; set; } = [];

    public string? SourceApp { get; set; }

    public string Hash { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    public long SizeBytes { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                return Text;
            }

            return Kind switch
            {
                ClipKind.Image => ImageWidth > 0 && ImageHeight > 0
                    ? $"图片 {ImageWidth}×{ImageHeight}"
                    : "图片",
                ClipKind.Files => FilePaths.Count == 0
                    ? "文件"
                    : string.Join("\n", FilePaths.Select(Path.GetFileName)),
                _ => string.Empty,
            };
        }
    }
}
