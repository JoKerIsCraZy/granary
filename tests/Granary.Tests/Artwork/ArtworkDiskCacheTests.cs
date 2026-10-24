using System.Runtime.ExceptionServices;
using Granary.Core.Artwork;
using Granary.Tests.Fakes;

namespace Granary.Tests.Artwork;

public sealed class ArtworkDiskCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_KeepsTheImageForTheNextStart()
    {
        await Create().WriteAsync("a", [1, 2, 3], TestContext.Current.CancellationToken);

        var read = await Create().ReadAsync("a", TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], read);
    }

    [Fact]
    public async Task Read_OfAnUnknownImage_IsAMiss()
    {
        Assert.Null(await Create().ReadAsync("missing", TestContext.Current.CancellationToken));
        Assert.False(Create().Contains("missing"));
    }

    [Fact]
    public async Task Read_OfAnUnknownImage_ThrowsNothingOnTheWay()
    {
        // A miss is the common case on a cold cache; a debugger reports every exception thrown, caught or not.
        var cache = Create();
        int thrown = 0;
        EventHandler<FirstChanceExceptionEventArgs> count = (_, e) =>
        {
            if (e.Exception is IOException && e.Exception.Message.Contains(_directory, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref thrown);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += count;
        try
        {
            await cache.ReadAsync("missing", TestContext.Current.CancellationToken);
            await cache.WriteAsync("other", [1], TestContext.Current.CancellationToken);
            await cache.ReadAsync("missing", TestContext.Current.CancellationToken);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= count;
        }

        Assert.Equal(0, thrown);
    }

    [Fact]
    public async Task Write_BeyondTheBudget_DeletesTheLeastRecentlyUsedImages()
    {
        var cache = Create(capacityBytes: 100);
        await cache.WriteAsync("old", new byte[40], TestContext.Current.CancellationToken);
        await cache.WriteAsync("newer", new byte[40], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(cache.PathFor("old"), _time.GetUtcNow().UtcDateTime.AddDays(-3));
        File.SetLastWriteTimeUtc(cache.PathFor("newer"), _time.GetUtcNow().UtcDateTime.AddDays(-2));

        await cache.WriteAsync("newest", new byte[40], TestContext.Current.CancellationToken);
        await cache.WhenTrimmed;

        Assert.False(cache.Contains("old"));
        Assert.True(cache.Contains("newer"));
        Assert.True(cache.Contains("newest"));
    }

    [Fact]
    public async Task Write_CountsTheImagesOfEarlierStarts()
    {
        var earlier = Create(capacityBytes: 1024);
        await earlier.WriteAsync("earlier-1", new byte[40], TestContext.Current.CancellationToken);
        await earlier.WriteAsync("earlier-2", new byte[40], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(earlier.PathFor("earlier-1"), _time.GetUtcNow().UtcDateTime.AddDays(-3));
        File.SetLastWriteTimeUtc(earlier.PathFor("earlier-2"), _time.GetUtcNow().UtcDateTime.AddDays(-2));
        var cache = Create(capacityBytes: 100);

        await cache.WriteAsync("now", new byte[40], TestContext.Current.CancellationToken);
        await cache.WhenTrimmed;

        Assert.False(cache.Contains("earlier-1"));
        Assert.True(cache.Contains("earlier-2"));
        Assert.True(cache.Contains("now"));
    }

    [Fact]
    public async Task Write_SweepsTemporaryFilesThatAWriteLeftBehind()
    {
        // A write the app did not finish (it was closed or crashed) leaves its temporary file.
        Directory.CreateDirectory(_directory);
        string abandoned = Path.Combine(_directory, "abandoned.0123.tmp");
        string inProgress = Path.Combine(_directory, "in-progress.4567.tmp");
        await File.WriteAllBytesAsync(abandoned, new byte[10], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(inProgress, new byte[10], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(abandoned, _time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(inProgress, _time.GetUtcNow().UtcDateTime);

        await Create().WriteAsync("a", [1], TestContext.Current.CancellationToken);

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(inProgress));
    }

    [Fact]
    public async Task Read_MarksTheImageAsRecentlyUsed()
    {
        var cache = Create(capacityBytes: 100);
        await cache.WriteAsync("read", new byte[40], TestContext.Current.CancellationToken);
        await cache.WriteAsync("unread", new byte[40], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(cache.PathFor("read"), _time.GetUtcNow().UtcDateTime.AddDays(-3));
        File.SetLastWriteTimeUtc(cache.PathFor("unread"), _time.GetUtcNow().UtcDateTime.AddDays(-2));

        await cache.ReadAsync("read", TestContext.Current.CancellationToken);
        await cache.WriteAsync("newest", new byte[40], TestContext.Current.CancellationToken);
        await cache.WhenTrimmed;

        Assert.True(cache.Contains("read"));
        Assert.False(cache.Contains("unread"));
    }

    [Fact]
    public async Task Remove_DeletesTheImage()
    {
        var cache = Create();
        await cache.WriteAsync("a", [1], TestContext.Current.CancellationToken);

        cache.Remove("a");

        Assert.False(cache.Contains("a"));
    }

    [Fact]
    public void PathFor_NamesFilesByAHashOfTheKey()
    {
        string path = Create().PathFor("https://nas.example/api/v2/artwork/movies/949/poster/w342.webp");

        Assert.Equal(_directory, Path.GetDirectoryName(path));
        Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(path));
    }

    private ArtworkDiskCache Create(long capacityBytes = 1024) =>
        new(_directory, capacityBytes, _logs.For<ArtworkDiskCache>(), _time);
}
