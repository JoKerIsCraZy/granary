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
}
