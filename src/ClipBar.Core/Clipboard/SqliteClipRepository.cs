using System.Text.Json;
using ClipBar.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipBar.Core.Clipboard;

public sealed class SqliteClipRepository : IClipRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteClipRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Deliberately NOT SqliteCacheMode.Shared: shared-cache mode makes SQLite take table-
        // level locks, and a concurrent write then fails with SQLITE_LOCKED ("database table is
        // locked") which busy_timeout does not retry — it only retries SQLITE_BUSY. Each
        // connection therefore gets its own page cache, and WAL lets readers run beside a writer.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <summary>
    /// Opens a connection with the busy timeout set. The pragma is per-connection, so it has to
    /// be applied on every open — without it a writer that collides with another writer fails
    /// immediately instead of waiting the few milliseconds the other transaction needs.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS clips (
                    id TEXT PRIMARY KEY NOT NULL,
                    kind INTEGER NOT NULL,
                    text TEXT,
                    html TEXT,
                    image_path TEXT,
                    file_paths TEXT,
                    source_app TEXT,
                    hash TEXT NOT NULL,
                    is_pinned INTEGER NOT NULL DEFAULT 0,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    size_bytes INTEGER NOT NULL DEFAULT 0,
                    image_width INTEGER NOT NULL DEFAULT 0,
                    image_height INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_clips_hash ON clips(hash);
                CREATE INDEX IF NOT EXISTS idx_clips_order ON clips(is_pinned DESC, updated_at DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ClipItem> AddOrTouchAsync(ClipItem item, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (item.CreatedAt == 0)
        {
            item.CreatedAt = now;
        }

        item.UpdatedAt = now;
        item.Hash = string.IsNullOrWhiteSpace(item.Hash) ? ClipHash.Compute(item) : item.Hash;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clips
                (id, kind, text, html, image_path, file_paths, source_app, hash, is_pinned,
                 created_at, updated_at, size_bytes, image_width, image_height)
            VALUES
                ($id, $kind, $text, $html, $imagePath, $filePaths, $sourceApp, $hash, $isPinned,
                 $createdAt, $updatedAt, $sizeBytes, $imageWidth, $imageHeight)
            ON CONFLICT(hash) DO UPDATE SET
                text = COALESCE(excluded.text, clips.text),
                html = COALESCE(excluded.html, clips.html),
                image_path = COALESCE(excluded.image_path, clips.image_path),
                file_paths = COALESCE(excluded.file_paths, clips.file_paths),
                source_app = COALESCE(excluded.source_app, clips.source_app),
                updated_at = excluded.updated_at,
                size_bytes = excluded.size_bytes,
                image_width = excluded.image_width,
                image_height = excluded.image_height
            RETURNING id, is_pinned, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$html", (object?)item.Html ?? DBNull.Value);
        command.Parameters.AddWithValue("$imagePath", (object?)item.ImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$filePaths", JsonSerializer.Serialize(item.FilePaths));
        command.Parameters.AddWithValue("$sourceApp", (object?)item.SourceApp ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", item.Hash);
        command.Parameters.AddWithValue("$isPinned", item.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt);
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt);
        command.Parameters.AddWithValue("$sizeBytes", item.SizeBytes);
        command.Parameters.AddWithValue("$imageWidth", item.ImageWidth);
        command.Parameters.AddWithValue("$imageHeight", item.ImageHeight);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            item.Id = reader.GetString(0);
            item.IsPinned = reader.GetInt32(1) != 0;
            item.CreatedAt = reader.GetInt64(2);
            item.UpdatedAt = reader.GetInt64(3);
        }

        return item;
    }

    public async Task<IReadOnlyList<ClipItem>> QueryAsync(
        ClipFilter filter,
        string? search,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new List<string>();
        if (filter != ClipFilter.All)
        {
            where.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)ToKind(filter));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(text LIKE $q ESCAPE '\\' OR source_app LIKE $q ESCAPE '\\' OR file_paths LIKE $q ESCAPE '\\')");
            command.Parameters.AddWithValue("$q", $"%{EscapeLike(search.Trim())}%");
        }

        command.CommandText = $"""
            SELECT id, kind, text, html, image_path, file_paths, source_app, hash,
                   is_pinned, created_at, updated_at, size_bytes, image_width, image_height
            FROM clips
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
            ORDER BY is_pinned DESC, updated_at DESC
            LIMIT (SELECT COUNT(*) FROM clips WHERE is_pinned = 1) + $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var result = new List<ClipItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadItem(reader));
        }

        return result;
    }

    public async Task<ClipItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, text, html, image_path, file_paths, source_app, hash,
                   is_pinned, created_at, updated_at, size_bytes, image_width, image_height
            FROM clips WHERE id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    public async Task SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET is_pinned = $pinned, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id RETURNING image_path;";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    public async Task<IReadOnlyList<string>> ClearAsync(bool includePinned = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = includePinned
            ? "SELECT image_path FROM clips WHERE image_path IS NOT NULL;"
            : "SELECT image_path FROM clips WHERE is_pinned = 0 AND image_path IS NOT NULL;";
        var images = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                images.Add(reader.GetString(0));
            }
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = includePinned ? "DELETE FROM clips;" : "DELETE FROM clips WHERE is_pinned = 0;";
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return images;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clips;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    public async Task<IReadOnlyList<string>> TrimAsync(int maxNonPinned, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (maxNonPinned < 1)
        {
            maxNonPinned = 1;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = """
            SELECT id, image_path FROM clips
            WHERE is_pinned = 0
            ORDER BY updated_at DESC
            LIMIT -1 OFFSET $offset;
            """;
        select.Parameters.AddWithValue("$offset", maxNonPinned);
        var ids = new List<string>();
        var images = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    images.Add(reader.GetString(1));
                }
            }
        }

        // SQLite caps the number of bound parameters (default 32766), and building one giant
        // "IN (...)" list would blow that with a large history. Delete in fixed-size batches;
        // they run inside the same transaction, so the trim stays atomic.
        const int batchSize = 200;
        for (var start = 0; start < ids.Count; start += batchSize)
        {
            var batch = ids.GetRange(start, Math.Min(batchSize, ids.Count - start));
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            var parameterNames = new List<string>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var name = "$id" + i;
                parameterNames.Add(name);
                delete.Parameters.AddWithValue(name, batch[i]);
            }

            delete.CommandText = $"DELETE FROM clips WHERE id IN ({string.Join(", ", parameterNames)});";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return images;
    }

    public async Task<IReadOnlyList<string>> GetImagePathsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT image_path FROM clips WHERE image_path IS NOT NULL;";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ClipKind ToKind(ClipFilter filter) => filter switch
    {
        ClipFilter.Text => ClipKind.Text,
        ClipFilter.Image => ClipKind.Image,
        ClipFilter.Files => ClipKind.Files,
        _ => ClipKind.Text,
    };

    private static ClipItem ReadItem(SqliteDataReader reader)
    {
        var item = new ClipItem
        {
            Id = reader.GetString(0),
            Kind = (ClipKind)reader.GetInt32(1),
            Text = reader.IsDBNull(2) ? null : reader.GetString(2),
            Html = reader.IsDBNull(3) ? null : reader.GetString(3),
            ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceApp = reader.IsDBNull(6) ? null : reader.GetString(6),
            Hash = reader.GetString(7),
            IsPinned = reader.GetInt32(8) != 0,
            CreatedAt = reader.GetInt64(9),
            UpdatedAt = reader.GetInt64(10),
            SizeBytes = reader.GetInt64(11),
            ImageWidth = reader.GetInt32(12),
            ImageHeight = reader.GetInt32(13),
        };

        if (!reader.IsDBNull(5))
        {
            try
            {
                item.FilePaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [];
            }
            catch (JsonException)
            {
                item.FilePaths = [];
            }
        }

        return item;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
