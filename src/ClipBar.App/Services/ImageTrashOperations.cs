namespace ClipBar.App.Services;

/// <summary>Coordinates the file moves that make a deleted image reversible.</summary>
public interface IImageTrashOperations
{
    string? MoveToTrash(string? imagePath);

    bool RestoreFromTrash(string trashPath, string imagesDirectory);

    void DeleteTrashEntry(string? trashPath);
}

/// <summary>Production adapter over the path-checked image store operations.</summary>
public sealed class ImageTrashOperations : IImageTrashOperations
{
    public static ImageTrashOperations Instance { get; } = new();

    private ImageTrashOperations()
    {
    }

    public string? MoveToTrash(string? imagePath) => ImageStore.MoveToTrash(imagePath);

    public bool RestoreFromTrash(string trashPath, string imagesDirectory)
        => ImageStore.RestoreFromTrash(trashPath, imagesDirectory);

    public void DeleteTrashEntry(string? trashPath) => ImageStore.DeleteTrashEntry(trashPath);
}
