using System.Globalization;
using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Images;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Downloads;

/// <summary>
/// The pictures of a download's chapters and seek bar: the manifest's blurs without the server, and while it answers the
/// chapter pictures and seek-bar previews of the file the download was made from, read as the download's own profile.
/// </summary>
public sealed class LocalPlaybackPreviewTests : IAsyncDisposable
{
    private const string TrickplayPath = LocalPlaybackFixture.WatchPath + "/trickplay";

    private readonly LocalPlaybackFixture _fixture = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WithoutTheServer_EachChapterShowsTheManifestsBlur()
    {
        _fixture.GoOffline();

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, "iC")], playback.Chapters);
    }

    [Fact]
    public async Task WithoutTheServer_NoPreviewsAreAskedFor()
    {
        _fixture.GoOffline();
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()));

        var playback = await _fixture.PlayingAsync();
        await _fixture.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Null(playback.Trickplay);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        Assert.DoesNotContain(_fixture.Harness.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOldManifestWithoutTheBlur_StillPlays_WithThePlaceholder()
    {
        var download = await _fixture.Harness.AddPlayableAsync();
        string manifest = Path.Combine(download.Folder, DownloadPaths.Manifest);
        string older = File.ReadAllText(manifest).Replace(", \"thumbnail_thumbhash\": \"iC\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("thumbnail_thumbhash", older, StringComparison.Ordinal);
        File.WriteAllText(manifest, older);
        _fixture.GoOffline();

        var playback = await _fixture.StartAsync();

        Assert.NotNull(playback);
        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, null)], playback.Chapters);
    }

    [Fact]
    public async Task WithTheServer_AChapterTheWatchDetailMatches_ShowsItsPicture()
    {
        // The server measured the chapter a little differently: within a second it is the same one.
        ServerHas(Chapter(0, 0.6, "/api/v2/chapters/0.webp"));

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, ArtworkUrl.Resolve(ScriptedSilo.Base, "/api/v2/chapters/0.webp"), "iC")],
            playback.Chapters);
    }

    [Theory]
    [InlineData("4567", 1, 0)] // another chapter's index
    [InlineData("4567", 0, 30)] // a rescan moved its start
    [InlineData("9999", 0, 0)] // another file of the title
    public async Task WithTheServer_AChapterTheWatchDetailDoesNotMatch_KeepsItsBlur(string fileId, int index, double start)
    {
        ServerHas(Chapter(index, start, "/api/v2/chapters/0.webp"), fileId: fileId);

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, "iC")], playback.Chapters);
    }

    [Fact]
    public async Task WithTheServer_ThePreviewsOfTheFileTheDownloadWasMadeFrom_AreReadAsItsAccountAndProfile()
    {
        ServerHas(trickplay: true);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()));

        var playback = await StartedAsync();

        var read = Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        Assert.Equal("?file_id=4567", read.Uri.Query);
        Assert.Equal(SiloAuth.Profile, read.Auth);
        Assert.Equal(("1", "7"), (read.AsAccount, read.AsProfile));
        var trickplay = playback.Trickplay!;
        Assert.Equal(("4567", 10_000, 10, 90), (trickplay.FileId, trickplay.IntervalMs, trickplay.Columns, trickplay.Count));
        Assert.Equal(ArtworkUrl.Resolve(ScriptedSilo.Base, "/api/v2/artwork/sheet0?exp=1&sig=a"), trickplay.Sheets[0]);
    }

    [Fact]
    public async Task AFileWithoutPreviews_IsNotAskedForThem()
    {
        ServerHas(trickplay: false);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()));

        var playback = await _fixture.PlayingAsync();

        Assert.Null(playback.Trickplay);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
    }

    [Fact]
    public async Task ThePreviews_AreReadAgainBeforeTheirAddressesExpire()
    {
        ServerHas(trickplay: true);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()), Ok(Previews()));
        var playback = await StartedAsync();
        int changes = 0;
        playback.TrickplayChanged += (_, _) => changes++;

        // They expire in an hour: read again five minutes before.
        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(54));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(1));

        await Until(() => changes == 1, "the previews are read again");
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath).Count);
    }

    [Fact]
    public async Task APreviewReadThatFails_IsTriedAgainAMinuteLater()
    {
        ServerHas(trickplay: true);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.NetworkError(), Ok(Previews()));
        int changes = 0;
        var playback = await _fixture.PlayingAsync();
        playback.TrickplayChanged += (_, _) => changes++;
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath).Count == 1, "the previews are asked for");
        Assert.Null(playback.Trickplay);

        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(1));

        await Until(() => changes == 1, "the previews are read on the second try");
        Assert.NotNull(playback.Trickplay);
    }

    [Fact]
    public async Task AnotherProfileActiveAtTheRefresh_EndsTheReadsQuietly()
    {
        ServerHas(trickplay: true);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()), Ok(Previews()));
        var playback = await StartedAsync();

        // The viewer switched profiles meanwhile: the download's previews are not asked for as the other one.
        _fixture.Silo.ActingProfile = () => "8";
        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(55));
        await Until(() => _fixture.Harness.Logs.Lines.Any(line => line.Contains("the server acts for another profile now", StringComparison.Ordinal)
            && line.Contains("seek-bar previews", StringComparison.Ordinal)), "the refresh is refused as the other profile");
        _fixture.Silo.ActingProfile = null;
        await _fixture.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        Assert.NotNull(playback.Trickplay);
        Assert.DoesNotContain(_fixture.Harness.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStop_EndsTheRefresh()
    {
        ServerHas(trickplay: true);
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(Previews()), Ok(Previews()));
        var playback = await StartedAsync();

        await playback.StopAsync();
        await _fixture.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
    }

    [Fact]
    public async Task TheStop_CallsOffAPreviewReadOnItsWay()
    {
        ServerHas(trickplay: true);
        var asked = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.OnCancellable(HttpMethod.Get, TrickplayPath, async cancellationToken =>
        {
            asked.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Never answers.");
        });
        var playback = await _fixture.PlayingAsync();
        var read = await asked.Task.WaitAsync(LocalPlaybackFixture.Token);

        await playback.StopAsync();

        Assert.True(read.IsCancellationRequested);
        Assert.Null(playback.Trickplay);
        Assert.DoesNotContain(_fixture.Harness.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    // Plays the download and waits for its previews to be read.
    private async Task<LocalPlayback> StartedAsync()
    {
        var playback = await _fixture.PlayingAsync();
        await Until(() => playback.Trickplay is not null, "the previews are read");
        return playback;
    }

    // The episode's watch detail with the file's chapters and whether it has previews.
    private void ServerHas(string chapters = "", bool trickplay = false, string fileId = "4567")
    {
        string version = $$"""
            "file_id": "{{fileId}}", "trickplay_available": {{(trickplay ? "true" : "false")}}, "chapters": [ {{chapters}} ],
            """;
        string watch = LocalPlaybackFixture.Watch().Replace("\"file_id\": \"4567\",", version, StringComparison.Ordinal);
        Assert.Contains("trickplay_available", watch, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(HttpStatusCode.OK, watch));
    }

    private static string Chapter(int index, double start, string thumbnail) => $$"""
        { "index": {{index}}, "title": "Cold Open", "start_seconds": {{start.ToString(CultureInfo.InvariantCulture)}}, "end_seconds": 142.5,
          "source": "embedded", "thumbnail_url": "{{thumbnail}}", "thumbnail_thumbhash": "iC" }
        """;

    // The file's previews, whose addresses expire an hour from now.
    private string Previews()
    {
        string expires = _fixture.Harness.Time.GetUtcNow().AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $$"""
            { "file_id": "4567", "interval_ms": 10000, "thumbnail_width": 320, "thumbnail_height": 180, "tile_columns": 10,
              "tile_rows": 10, "thumbnail_count": 90, "sheets": [ { "index": 0, "url": "/api/v2/artwork/sheet0?exp=1&sig=a" } ],
              "expires_at": "{{expires}}" }
            """;
    }
}
