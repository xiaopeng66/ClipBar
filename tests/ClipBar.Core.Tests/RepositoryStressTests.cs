using System.Collections.Concurrent;
using System.Diagnostics;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipBar.Core.Tests;

/// <summary>
/// Plan §5.2 T-20 / T-24 / T-25 / T-26 / T-27 / T-28: capacity and concurrency. Every test gets
/// its own database under the temp folder — the real <c>%LOCALAPPDATA%\ClipBar</c> is never
/// touched (plan §6 red line).
/// </summary>
public sealed class RepositoryStressTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "clipbar-stress", Guid.NewGuid().ToString("N")[..8]);

    private static async Task<SqliteClipRepository> NewRepositoryAsync(string root)
    {
        Directory.CreateDirectory(root);
        var repository = new SqliteClipRepository(Path.Combine(root, "history.db"));
        await repository.InitializeAsync();
        return repository;
    }

    private static void Cleanup(string root)
    {
        // Pooled connections keep the file handle open; release them before deleting.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ClipItem Text(string text, string? source = "stress.exe") => new()
    {
        Kind = ClipKind.Text,
        Text = text,
        SourceApp = source,
    };

    // ---- T-20: clipboard flood -------------------------------------------

    [Fact]
    public async Task TwoThousandCapturesKeepTheHistoryAtTheCap()
    {
        // What ClipboardMonitor does per capture: AddOrTouch, then Trim to MaxItems. 2000 clips
        // at 5 ms is ~10 s of real time, so the loop drives the repository directly.
        const int maxItems = 500;
        const int captures = 2000;

        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < captures; i++)
            {
                await repository.AddOrTouchAsync(Text($"clip-{i}"));
                await repository.TrimAsync(maxItems);
            }

            stopwatch.Stop();

            var count = await repository.CountAsync();
            Assert.Equal(maxItems, count);

            // The survivors are the newest ones. Rows captured inside the same millisecond tie
            // on updated_at and the tiebreak is arbitrary, so allow a little slack at the edge
            // instead of pinning an exact index.
            var items = await repository.QueryAsync(ClipFilter.All, null, 500);
            Assert.Equal(maxItems, items.Count);
            Assert.Contains(items, x => x.Text == $"clip-{captures - 1}");

            var indexes = items
                .Select(x => int.Parse(x.Text!["clip-".Length..]))
                .ToList();
            Assert.True(
                indexes.Min() >= captures - maxItems - 100,
                $"最早的幸存条目是 clip-{indexes.Min()}，淘汰没有跟上");

            // 2000 insert + trim cycles should be seconds, not minutes. Generous bound: this is
            // a shared machine and the suite runs other collections in parallel.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"耗时 {stopwatch.Elapsed}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- T-24: trimming 8000 rows ----------------------------------------

    [Fact]
    public async Task TrimmingEightThousandRowsKeepsEveryPinnedItemAndStaysFast()
    {
        const int maxNonPinned = 5000;
        const int inserted = 8000;
        const int pinnedCount = 50;

        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);

            for (var i = 0; i < pinnedCount; i++)
            {
                var pinned = await repository.AddOrTouchAsync(Text($"pinned-{i}"));
                await repository.SetPinnedAsync(pinned.Id, true);
            }

            for (var i = 0; i < inserted; i++)
            {
                await repository.AddOrTouchAsync(Text($"plain-{i}"));
            }

            Assert.Equal(pinnedCount + inserted, await repository.CountAsync());

            var stopwatch = Stopwatch.StartNew();
            var removed = await repository.TrimAsync(maxNonPinned);
            stopwatch.Stop();

            // Non-pinned rows carry no image path, so nothing should be reported for deletion.
            Assert.Empty(removed);
            Assert.Equal(pinnedCount + maxNonPinned, await repository.CountAsync());

            // A single IN(...) list of 3000 ids would exceed SQLite's bound-parameter cap, so the
            // trim deletes in batches. This is the assertion that catches a regression there.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Trim 耗时 {stopwatch.Elapsed}");

            var pinnedSurvivors = (await repository.QueryAsync(ClipFilter.All, "pinned-", 5000))
                .Where(x => x.IsPinned)
                .Count();
            Assert.Equal(pinnedCount, pinnedSurvivors);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- T-25: concurrent writers ----------------------------------------

    [Fact]
    public async Task EightWritersNeverHitSqliteBusyAndNeverLoseARow()
    {
        const int threads = 8;
        const int perThread = 200;

        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            var failures = new ConcurrentQueue<Exception>();

            var workers = Enumerable.Range(0, threads).Select(thread => Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < perThread; i++)
                    {
                        await repository.AddOrTouchAsync(Text($"t{thread}-{i}"));
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })).ToArray();

            await Task.WhenAll(workers);

            Assert.Empty(failures);
            Assert.Equal(threads * perThread, await repository.CountAsync());

            // Re-running the same texts is a pure touch: no duplicates may appear.
            var second = Enumerable.Range(0, threads).Select(thread => Task.Run(async () =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    await repository.AddOrTouchAsync(Text($"t{thread}-{i}"));
                }
            })).ToArray();
            await Task.WhenAll(second);

            Assert.Equal(threads * perThread, await repository.CountAsync());
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- T-26: trim racing against queries --------------------------------

    [Fact]
    public async Task ConcurrentTrimAndQueryDoNotThrow()
    {
        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            var failures = new ConcurrentQueue<Exception>();
            using var stop = new CancellationTokenSource();

            var writer = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 400 && !stop.IsCancellationRequested; i++)
                    {
                        await repository.AddOrTouchAsync(Text($"w-{i}"));
                        if (i % 4 == 0)
                        {
                            await repository.TrimAsync(50);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await repository.QueryAsync(ClipFilter.All, null, 200);
                        await repository.CountAsync();
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })).ToArray();

            await writer;
            stop.Cancel();
            await Task.WhenAll(readers);

            Assert.Empty(failures);

            // Every row a reader saw may since have been trimmed; what must hold is that the
            // repository is still usable afterwards.
            Assert.InRange(await repository.CountAsync(), 1, 400);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- T-27: rows damaged behind the app's back ------------------------

    [Fact]
    public async Task MalformedRowsDegradeInsteadOfCrashingTheQuery()
    {
        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            var good = await repository.AddOrTouchAsync(Text("healthy"));
            var badKind = await repository.AddOrTouchAsync(Text("kind-99"));
            var badJson = await repository.AddOrTouchAsync(Text("bad-json"));
            var jsonNull = await repository.AddOrTouchAsync(Text("json-null"));
            var jsonString = await repository.AddOrTouchAsync(Text("json-string"));

            // Simulate a hand-edited or half-migrated database.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "history.db")}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE clips SET kind = 99 WHERE id = $kind;
                    UPDATE clips SET file_paths = 'not-json' WHERE id = $badJson;
                    UPDATE clips SET file_paths = 'null' WHERE id = $jsonNull;
                    UPDATE clips SET file_paths = '"a string"' WHERE id = $jsonString;
                    """;
                command.Parameters.AddWithValue("$kind", badKind.Id);
                command.Parameters.AddWithValue("$badJson", badJson.Id);
                command.Parameters.AddWithValue("$jsonNull", jsonNull.Id);
                command.Parameters.AddWithValue("$jsonString", jsonString.Id);
                await command.ExecuteNonQueryAsync();
            }

            var items = await repository.QueryAsync(ClipFilter.All, null, 500);

            Assert.Equal(5, items.Count);

            // An unknown kind is surfaced as-is; the UI's filter switch simply never matches it.
            var weird = Assert.Single(items, x => x.Id == badKind.Id);
            Assert.Equal(99, (int)weird.Kind);

            // Every unparsable file_paths value degrades to an empty list rather than throwing.
            foreach (var id in new[] { badJson.Id, jsonNull.Id, jsonString.Id })
            {
                var item = Assert.Single(items, x => x.Id == id);
                Assert.Empty(item.FilePaths);
            }

            // Searching still works over the damaged table.
            Assert.Single(await repository.QueryAsync(ClipFilter.All, "healthy"));

            // ...and so does reading a single row.
            Assert.NotNull(await repository.GetAsync(good.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- T-28: search storm ----------------------------------------------

    [Fact]
    public async Task TypingAQueryOneCharacterAtATimeStaysResponsive()
    {
        // 500 rows is the panel's page size; the search box refreshes on every keystroke.
        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            for (var i = 0; i < 500; i++)
            {
                await repository.AddOrTouchAsync(Text($"第{i}条 剪贴板历史 {Guid.NewGuid():N}"));
            }

            var typed = "剪贴板历史";
            var stopwatch = Stopwatch.StartNew();
            for (var length = 1; length <= typed.Length; length++)
            {
                var query = typed[..length];
                var results = await repository.QueryAsync(ClipFilter.All, query, 500);
                Assert.NotEmpty(results);
            }

            stopwatch.Stop();

            // Five incremental queries over 500 rows: milliseconds, not seconds.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"搜索耗时 {stopwatch.Elapsed}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- search wildcard handling (F-08 / LIKE escaping) ------------------

    [Fact]
    public async Task SearchTreatsWildcardsAsLiterals()
    {
        var root = NewRoot();
        try
        {
            var repository = await NewRepositoryAsync(root);
            await repository.AddOrTouchAsync(Text("100% done"));
            await repository.AddOrTouchAsync(Text("under_score"));
            await repository.AddOrTouchAsync(Text(@"C:\temp\a.txt"));
            await repository.AddOrTouchAsync(Text("nothing like it"));

            // "%" and "_" would otherwise match anything; "\" would break the ESCAPE clause.
            var percent = await repository.QueryAsync(ClipFilter.All, "%");
            var underscore = await repository.QueryAsync(ClipFilter.All, "_");
            var backslash = await repository.QueryAsync(ClipFilter.All, @"\");

            Assert.Single(percent);
            Assert.Equal("100% done", percent[0].Text);
            Assert.Single(underscore);
            Assert.Equal("under_score", underscore[0].Text);
            Assert.Single(backslash);
            Assert.Equal(@"C:\temp\a.txt", backslash[0].Text);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
