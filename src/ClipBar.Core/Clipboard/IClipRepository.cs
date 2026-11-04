using ClipBar.Core.Models;

namespace ClipBar.Core.Clipboard;

public interface IClipRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<ClipItem> AddOrTouchAsync(ClipItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="limit"/> budgets the NON-PINNED rows; pinned rows are always included on
    /// top of it, so the caller passes MaxItems and still sees every pinned row.
    /// </summary>
    Task<IReadOnlyList<ClipItem>> QueryAsync(
        ClipFilter filter,
        string? search,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<ClipItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default);

    Task<string?> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ClearAsync(bool includePinned = false, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> TrimAsync(int maxNonPinned, CancellationToken cancellationToken = default);

    /// <summary>Every image path still referenced by a row, for the orphan sweep at startup.</summary>
    Task<IReadOnlyList<string>> GetImagePathsAsync(CancellationToken cancellationToken = default);
}
