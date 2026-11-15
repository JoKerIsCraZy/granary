using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Granary.Core.Downloads;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>A download's manifest, artwork and subtitles: what is kept, and what is left out (too large, stalled).</summary>
public sealed class DownloadAssetsTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadAssets Assets(long? maxBytes = null) =>
        maxBytes is { } limit
            ? new DownloadAssets(_h.Api, _h.Time, _h.Logs.For<DownloadAssets>()) { MaxBytes = limit }
            : new DownloadAssets(_h.Api, _h.Time, _h.Logs.For<DownloadAssets>());

    private static string[] FilesIn(StoredDownload download) => [.. Directory.EnumerateFiles(download.Folder).Select(Path.GetFileName).OfType<string>().Order()];

    [Fact]
    public async Task AnAssetThatSaysItIsLargerThanTheCap_IsLeftOut()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ArtworkPath("dl_1", "poster"), () =>
        {
            var content = new ByteArrayContent(PosterBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Headers.ContentLength = DownloadAssets.MaxAssetBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        var fetched = await Assets().FetchAsync(ScriptedSilo.Base, stored, Token);

        Assert.False(fetched.ArtworkFiles.ContainsKey("poster"));
        Assert.Equal("backdrop.jpg", fetched.ArtworkFiles["backdrop"]);
        Assert.DoesNotContain("poster.jpg", FilesIn(stored));
    }

    [Fact]
    public async Task AnAssetThatRunsPastTheCap_IsLeftOut_AndLeavesNoFileBehind()
    {
        var stored = await _h.AddAsync("dl_1");
        // No length named: the body runs on past the cap.
        _h.Silo.On(HttpMethod.Get, ArtworkPath("dl_1", "poster"),
            DownloadPayloads.Streamed(HttpStatusCode.OK, new MemoryStream(DownloadPayloads.Source(64)), null, null));

        var fetched = await Assets(maxBytes: 32).FetchAsync(ScriptedSilo.Base, stored, Token);

        Assert.False(fetched.ArtworkFiles.ContainsKey("poster"));
        Assert.DoesNotContain(FilesIn(stored), name => name.StartsWith("poster", StringComparison.Ordinal));
        Assert.Contains(_h.Logs.Lines, line => line.Contains("poster of download dl_1 did not come", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStalledAsset_IsGivenUpAfterAMinute_AndTheRestIsKept()
    {
        var stored = await _h.AddAsync("dl_1");
        var stalled = new TestMediaStream(PosterBytes) { StallAfter = 0 };
        _h.Silo.On(HttpMethod.Get, ArtworkPath("dl_1", "poster"), DownloadPayloads.Streamed(HttpStatusCode.OK, stalled, null, null));

        var fetch = Assets().FetchAsync(ScriptedSilo.Base, stored, Token);
        await stalled.Stalling.WaitAsync(TimeSpan.FromSeconds(5), Token);
        _h.Time.Advance(DownloadAssets.StallTimeout - TimeSpan.FromSeconds(1));
        Assert.False(fetch.IsCompleted);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        var fetched = await fetch.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.False(fetched.ArtworkFiles.ContainsKey("poster"));
        Assert.Equal("series_poster.jpg", fetched.ArtworkFiles["series_poster"]);
        Assert.Equal("r1", fetched.SubtitleRevisions["external:0"]);
        Assert.DoesNotContain(FilesIn(stored), name => name.StartsWith("poster", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AManifestOfAnotherDownload_IsRefused_AndNotKept()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_2")));

        await Assert.ThrowsAsync<JsonException>(() => Assets().FetchAsync(ScriptedSilo.Base, stored, Token));

        Assert.DoesNotContain(DownloadPaths.Manifest, FilesIn(stored));
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, ArtworkPath("dl_1", "poster")));
    }

    // The server has new bytes for a download whose old ones are on this PC: their manifest, tracks and subtitles describe
    // bytes this PC does not have, so the folder keeps the ones of its media.
    [Fact]
    public async Task AManifestOfAnotherRevision_IsNotKeptBesideTheMediaItDoesNotDescribe()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with { MediaFile = "media.mkv" });
        await System.IO.File.WriteAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), DownloadPayloads.Manifest("dl_1"), Token);
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1", "mp4", revision: 2)));

        await Assert.ThrowsAsync<ManifestRevisionException>(() => Assets().FetchAsync(ScriptedSilo.Base, stored, Token));

        Assert.Equal(DownloadPayloads.Manifest("dl_1"), await System.IO.File.ReadAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), Token));
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, ArtworkPath("dl_1", "poster")));
    }

    // Before its first transfer a download has no bytes the manifest could fail to describe: the file it fetches next is the
    // server's current one, which that manifest names.
    [Fact]
    public async Task AManifestOfANewerRevision_IsKeptBeforeTheFirstTransfer()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1", revision: 2)));

        var fetched = await Assets().FetchAsync(ScriptedSilo.Base, stored, Token);

        Assert.Equal(2, fetched.Manifest.Revision);
        Assert.Contains(DownloadPaths.Manifest, FilesIn(stored));
    }

    // The registry named revision 2 already, and the manifest still describes revision 1: its size and tracks are not the
    // bytes the transfer fetches next. It is not kept, and the transfer tries again later.
    [Fact]
    public async Task AManifestOfAnOlderRevision_IsNotKept_BeforeTheFirstTransferEither()
    {
        var stored = await _h.AddAsync("dl_1", change: r => r with { Revision = 2 });
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1", revision: 1)));

        await Assert.ThrowsAsync<ManifestRevisionException>(() => Assets().FetchAsync(ScriptedSilo.Base, stored, Token));

        Assert.DoesNotContain(DownloadPaths.Manifest, FilesIn(stored));
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, ArtworkPath("dl_1", "poster")));
    }

    // The day-old read of a finished download's manifest finds another revision: the registry is read again, which replaces
    // the download once it lists the new bytes, rather than the old media being shown with the new manifest.
    [Fact]
    public async Task ARefreshThatFindsAnotherRevision_ReadsTheRegistryAgain_AndKeepsTheOldManifest()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with { MediaFile = "media.mkv" });
        await System.IO.File.WriteAllBytesAsync(Path.Combine(stored.Folder, "media.mkv"), DownloadPayloads.Source(3000), Token);
        await System.IO.File.WriteAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), DownloadPayloads.Manifest("dl_1"), Token);
        _h.List(DownloadPayloads.Entry("dl_1", "completed"));
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1", "mp4", revision: 2)));

        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count >= 2, "the registry is read again");
        await SettleAsync(queue);

        Assert.Equal(DownloadPayloads.Manifest("dl_1"), await System.IO.File.ReadAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), Token));
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((DownloadState.Completed, (DateTimeOffset?)null), (record.State, record.ManifestReadAt));
    }
}
