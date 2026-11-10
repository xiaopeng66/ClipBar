namespace ClipBar.App.Services;

/// <summary>Clears a compact window reference only for that window's own close callback.</summary>
internal static class CompactWindowLifetime
{
    public static void ClearIfCurrent<T>(ref T? current, T closed) where T : class
    {
        if (ReferenceEquals(current, closed))
        {
            current = null;
        }
    }
}
