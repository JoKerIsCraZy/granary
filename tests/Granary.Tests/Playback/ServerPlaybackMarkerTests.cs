using System.Text.RegularExpressions;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>The marked parts of the playing file (docs/api-contract.md, 11.1), read again with the file named once it plays.</summary>
public sealed class ServerPlaybackMarkerTests
{
    private const string WatchPath = "api/v2/watch/" + MovieId;

    private static readonly PlaybackMarker Intro = new(WatchMarkerKinds.Intro, 0, 90);
    private static readonly PlaybackMarker Credits = new(WatchMarkerKinds.Credits, 6000, 6100);

    // The same answer with a credits segment the server found on demand for the named file.
    private static readonly string WithCredits = PlaybackPayloads.WatchStateOk.Replace(
        "\"marker_segments\": [",
        "\"marker_segments\": [ { \"kind\": \"credits\", \"start_seconds\": 6000, \"end_seconds\": 6100 },",
        StringComparison.Ordinal);

    private readonly ServerPlaybackFixture _fixture = new();

    [Fact]
    public async Task TheIntro_HasItsSegmentsPicture()
    {
        var playback = await _fixture.StartAsync();

        Assert.Equal(new Uri("https://images.example.test/intro.webp"), Assert.Single(playback.Markers).ThumbnailUrl);
    }

    [Fact]
    public async Task ThePreviews_AreReadOnceTheFilePlays_AndAgainBeforeTheyExpire()
    {
        const string TrickplayPath = WatchPath + "/trickplay";
        string expires = _fixture.Time.GetUtcNow().AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        string manifest = $$"""
            { "file_id": "42", "interval_ms": 10000, "thumbnail_width": 320, "thumbnail_height": 180, "tile_columns": 10,
              "tile_rows": 10, "thumbnail_count": 90, "sheets": [ { "index": 0, "url": "/api/v2/artwork/t0?exp=1&sig=a" } ],
              "expires_at": "{{expires}}" }
            """;
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(PlaybackPayloads.WatchStateOk.Replace(
            "\"trickplay_available\": false", "\"trickplay_available\": true", StringComparison.Ordinal)));
        _fixture.Silo.On(HttpMethod.Get, TrickplayPath, Ok(manifest), Ok(manifest));
        var playback = await _fixture.StartAsync();
        int changes = 0;
        playback.TrickplayChanged += (_, _) => changes++;
        Assert.Null(playback.Trickplay);

        _fixture.Engine.RaiseState(Frame(12.5));
        await Until(() => changes == 1, "the previews are read");

        var trickplay = playback.Trickplay!;
        Assert.Equal((10_000, 10, 90), (trickplay.IntervalMs, trickplay.Columns, trickplay.Count));
        Assert.Equal("?file_id=42", _fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath)[0].Uri.Query);

        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(54));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(1));
        await Until(() => changes == 2, "the previews are read again before they expire");
    }

    [Fact]
    public async Task TheChapters_AreThePlayingFilesInOrder_WithTheirPicturesOnTheServer()
    {
        string chapters = Regex.Replace(PlaybackPayloads.WatchStateOk, @"""chapters"": \[[^\]]*\]", """
            "chapters": [
              { "index": 2, "title": "Heist", "start_seconds": 300, "end_seconds": 900, "thumbnail_url": "/api/v2/chapters/2.webp" },
              { "index": 1, "title": "Opening", "start_seconds": 0, "end_seconds": 300 },
              { "index": 3, "title": "Nothing", "start_seconds": 900, "end_seconds": 900 }
            ]
            """);
        Assert.Contains("Heist", chapters, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(chapters));

        var playback = await _fixture.StartAsync();

        Assert.Equal(
            [
                new PlaybackChapter("Opening", 0, 300, null),
                new PlaybackChapter("Heist", 300, 900, ArtworkUrl.Resolve(ScriptedSilo.Base, "/api/v2/chapters/2.webp")),
            ],
            playback.Chapters);
    }

    [Fact]
    public async Task TheMarkers_AreTheWatchDetails_UntilTheFileIsReadAgainOnceItPlays()
    {
        var reread = new TaskCompletionSource();
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(PlaybackPayloads.WatchStateOk), After(reread.Task, Ok(WithCredits)));
        var playback = await _fixture.StartAsync();
        int changes = 0;
        playback.MarkersChanged += (_, _) => changes++;

        Assert.Equal([Intro], playback.Markers);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, WatchPath));

        _fixture.Engine.RaiseState(Frame(12.5));
        reread.SetResult();
        await Until(() => changes == 1, "the read markers are taken");

        Assert.Equal([Intro, Credits], playback.Markers);
        Assert.Equal("?file_id=42", _fixture.Silo.SentTo(HttpMethod.Get, WatchPath)[^1].Uri.Query);
    }

    [Fact]
    public async Task TheFile_IsReadAgainOnce_AndAnAnswerWithNothingNewChangesNothing()
    {
        var playback = await _fixture.PlayingAsync();
        int changes = 0;
        playback.MarkersChanged += (_, _) => changes++;
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Get, WatchPath).Count == 2, "the file is read again");

        _fixture.Engine.RaiseState(Frame(30, paused: true));
        _fixture.Engine.RaiseState(Frame(30));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, WatchPath).Count);
        Assert.Equal(0, changes);
        Assert.Equal([Intro], playback.Markers);
    }

    [Fact]
    public async Task AFailedRead_KeepsTheWatchDetailsMarkers()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(PlaybackPayloads.WatchStateOk), ScriptedSilo.NetworkError());

        var playback = await _fixture.PlayingAsync();
        await Until(() => _fixture.Logs.Lines.Any(l => l.Contains("markers", StringComparison.Ordinal)), "the failed read is logged");

        Assert.Equal([Intro], playback.Markers);
        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
    }
}
