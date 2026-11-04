using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;

namespace ClipBar.Core.Tests;

public sealed class SqliteClipRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "clipbar-tests",
        Guid.NewGuid().ToString("N"));

    private SqliteClipRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteClipRepository(Path.Combine(_directory, "history.db"));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddText_CanQueryIt()
    {
        await _repository.AddOrTouchAsync(NewText("hello"));

        var items = await _repository.QueryAsync(ClipFilter.All, null);

        var item = Assert.Single(items);
        Assert.Equal(ClipKind.Text, item.Kind);
        Assert.Equal("hello", item.Text);
        Assert.False(item.IsPinned);
    }

    [Fact]
    public async Task DuplicateText_IsTouchedNotDuplicated()
    {
        var first = await _repository.AddOrTouchAsync(NewText("same"));
        await Task.Delay(5);
        var second = await _repository.AddOrTouchAsync(NewText("same"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _repository.CountAsync());
        Assert.True(second.UpdatedAt >= first.UpdatedAt);
    }

    [Fact]
    public async Task Filters_ReturnOnlyMatchingKinds()
    {
        await _repository.AddOrTouchAsync(NewText("text"));
        await _repository.AddOrTouchAsync(new ClipItem
        {
            Kind = ClipKind.Image,
            Text = "图片 10×10",
            ImagePath = Path.Combine(_directory, "images", "a.png"),
            ImageWidth = 10,
            ImageHeight = 10,
        });
        await _repository.AddOrTouchAsync(new ClipItem
        {
            Kind = ClipKind.Files,
            Text = @"C:\temp\a.txt",
            FilePaths = [@"C:\temp\a.txt"],
        });

        Assert.Single(await _repository.QueryAsync(ClipFilter.Text, null));
        Assert.Single(await _repository.QueryAsync(ClipFilter.Image, null));
        Assert.Single(await _repository.QueryAsync(ClipFilter.Files, null));
        Assert.Equal(3, (await _repository.QueryAsync(ClipFilter.All, null)).Count);
    }

    [Fact]
    public async Task Search_MatchesTextAndSource()
    {
        await _repository.AddOrTouchAsync(NewText("alpha", "notepad.exe"));
        await _repository.AddOrTouchAsync(NewText("beta", "chrome.exe"));

        var byText = await _repository.QueryAsync(ClipFilter.All, "alph");
        var bySource = await _repository.QueryAsync(ClipFilter.All, "chrome");

        Assert.Single(byText);
        Assert.Equal("alpha", byText[0].Text);
        Assert.Single(bySource);
        Assert.Equal("beta", bySource[0].Text);
    }

    [Fact]
    public async Task PinnedItems_ComeFirstAndSurviveTrim()
    {
        var pinned = await _repository.AddOrTouchAsync(NewText("keep"));
        await _repository.SetPinnedAsync(pinned.Id, true);
        await _repository.AddOrTouchAsync(NewText("one"));
        await _repository.AddOrTouchAsync(NewText("two"));
        await _repository.AddOrTouchAsync(NewText("three"));

        var trimmed = await _repository.TrimAsync(maxNonPinned: 2);
        var items = await _repository.QueryAsync(ClipFilter.All, null);

        Assert.Empty(trimmed);
        Assert.Equal(3, items.Count);
        Assert.True(items[0].IsPinned);
        Assert.Equal("keep", items[0].Text);
    }

    [Fact]
    public async Task Trim_RemovesOldestNonPinned()
    {
        await _repository.AddOrTouchAsync(NewText("one"));
        await Task.Delay(5);
        await _repository.AddOrTouchAsync(NewText("two"));
        await Task.Delay(5);
        await _repository.AddOrTouchAsync(NewText("three"));

        await _repository.TrimAsync(maxNonPinned: 2);
        var items = await _repository.QueryAsync(ClipFilter.All, null);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, x => x.Text == "one");
    }

    [Fact]
    public async Task Delete_ReturnsImagePathAndRemovesRow()
    {
        var imagePath = Path.Combine(_directory, "images", "b.png");
        var item = await _repository.AddOrTouchAsync(new ClipItem
        {
            Kind = ClipKind.Image,
            Text = "图片",
            ImagePath = imagePath,
        });

        var deletedPath = await _repository.DeleteAsync(item.Id);

        Assert.Equal(imagePath, deletedPath);
        Assert.Null(await _repository.GetAsync(item.Id));
    }

    [Fact]
    public async Task Clear_KeepsPinnedWhenRequested()
    {
        var pinned = await _repository.AddOrTouchAsync(NewText("keep"));
        await _repository.SetPinnedAsync(pinned.Id, true);
        await _repository.AddOrTouchAsync(NewText("drop"));

        await _repository.ClearAsync(includePinned: false);
        var items = await _repository.QueryAsync(ClipFilter.All, null);

        var item = Assert.Single(items);
        Assert.Equal("keep", item.Text);
    }

    [Fact]
    public async Task PinnedRowsAreReturnedBeyondTheNonPinnedLimit()
    {
        // The limit budgets non-pinned rows only: pinned rows must appear even when they exceed
        // the caller's budget, otherwise a long pin list silently hides history (P-0 family).
        for (var i = 0; i < 5; i++)
        {
            await _repository.AddOrTouchAsync(NewText($"plain-{i}"));
        }

        for (var i = 0; i < 3; i++)
        {
            var pinned = NewText($"pinned-{i}");
            await _repository.AddOrTouchAsync(pinned);
            await _repository.SetPinnedAsync(pinned.Id, true);
        }

        var items = await _repository.QueryAsync(ClipFilter.All, null, limit: 5);

        Assert.Equal(8, items.Count);
        Assert.Equal(3, items.Count(x => x.IsPinned));
        Assert.Equal(5, items.Count(x => !x.IsPinned));
        // Pinned sort first.
        Assert.True(items.Take(3).All(x => x.IsPinned));
    }

    private static ClipItem NewText(string text, string? source = "test.exe") => new()
    {
        Kind = ClipKind.Text,
        Text = text,
        SourceApp = source,
    };
}
