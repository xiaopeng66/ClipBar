namespace ClipBar.App.Services;

/// <summary>
/// Serializes clipboard image capture (file creation plus row insertion) with history mutations
/// that remove or restore image files. Without this gate, a capture can reuse a content-addressed
/// PNG while a concurrent delete/clear moves that same path away.
/// </summary>
internal static class ImageReferenceMutationGate
{
    public static SemaphoreSlim Semaphore { get; } = new(1, 1);
}
