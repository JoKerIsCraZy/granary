using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>Subtitles during a server playback (docs/api-contract.md, 8.8 and 9.6), as Silo's Apple client shows them.</summary>
public sealed class ServerPlaybackSubtitleTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    private static readonly string SubtitlePrefsPath = "api/v2/subtitle-prefs/" + SiloHttp.PathSegment("movie:heat-1995", "key");

    // The inventory of the direct play: 0 external SRT (English), 1 embedded ASS (English signs, forced), 2 embedded PGS (Japanese),
    // 3 embedded DVD (French, burn-in only), 4 downloaded SRT (Spanish). The file lists the embedded ones at FFmpeg
    // streams 3, 4 and 5.
    private const string Sidecar0 = "/api/v2/stream/" + Session + "/subtitles/0.vtt?file_id=42";

    public ServerPlaybackSubtitleTests()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(WithSubtitleTracks(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, SubtitlePrefsPath, ScriptedSilo.NoContent());
        _fixture.Engine.EmbeddedSubtitles[3] = 7;
        _fixture.Engine.EmbeddedSubtitles[4] = 8;
    }

    private static string WithSubtitleTracks(string watch, string extra = "")
    {
        string tracks = new Regex(@"(""audio_tracks"":\s*\[[^\]]*\])").Replace(watch, "$1, \"subtitle_tracks\": ["
            + "{ \"index\": 3, \"language\": \"eng\", \"codec\": \"ass\", \"forced\": true },"
            + "{ \"index\": 0, \"language\": \"eng\", \"codec\": \"srt\", \"external\": true },"
            + "{ \"index\": 4, \"language\": \"jpn\", \"codec\": \"hdmv_pgs_subtitle\" },"
            + "{ \"index\": 5, \"language\": \"fre\", \"codec\": \"dvd_subtitle\" } ]" + extra, 1);
        Assert.Contains("subtitle_tracks", tracks, StringComparison.Ordinal);
        return tracks;
    }

    // The direct play showing its external English SRT from the plan's artifact.
    private static string ShowingTheSidecar(string decision = PlaybackPayloads.StartOpaqueIds) => Showing(decision, "render", 0,
        $"\"artifact\": {{ \"url\": \"{Sidecar0}\", \"mime_type\": \"text/vtt\", \"format\": \"vtt\", \"timing_origin_seconds\": 0 }},");

    private static string Showing(string decision, string mode, int track, string extra = "")
    {
        string subtitle = new Regex(@"(""subtitle"":\s*\{\s*)""mode"":\s*""off"",").Replace(decision,
            $"$1\"mode\": \"{mode}\", \"track_id\": \"file:42:subtitle:{track}\", {extra}", 1);
        string selected = new Regex(@"""selected_tracks"":\s*\{").Replace(subtitle,
            $"\"selected_tracks\": {{ \"subtitle\": {{ \"id\": \"file:42:subtitle:{track}\", \"index\": {track} }},", 1);
        Assert.Contains($"\"mode\": \"{mode}\"", selected, StringComparison.Ordinal);
        Assert.Contains("\"subtitle\": { \"id\"", selected, StringComparison.Ordinal);
        return selected;
    }

    private JsonElement SavedChoice(int index = 0) => _fixture.Body(HttpMethod.Put, SubtitlePrefsPath, index);

    [Fact]
    public async Task AStart_SendsTheAutomaticChoice()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(WithSubtitleTracks(PlaybackPayloads.WatchStateOk).Replace("\"effective_subtitle_language\": \"eng\",",
                "\"effective_subtitle_language\": \"eng\", \"effective_subtitle_mode\": \"always\",", StringComparison.Ordinal)));

        await _fixture.StartAsync();

        // English always: the full external SRT before the embedded forced signs.
        Assert.Equal(0, _fixture.Body(HttpMethod.Post, StartPath).GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task AudioInThePreferredLanguage_StartsWithItsForcedTrackOnly()
    {
        // English audio, English preferred, auto, forced subtitles on (the default): the signs.
        await _fixture.StartAsync();

        Assert.Equal(1, _fixture.Body(HttpMethod.Post, StartPath).GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task AStartWithoutASubtitle_LeavesTheTrackOut()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(WithSubtitleTracks(PlaybackPayloads.WatchStateOk).Replace("\"effective_subtitle_language\": \"eng\",",
                "\"effective_subtitle_language\": \"eng\", \"effective_show_forced_subtitles\": false,", StringComparison.Ordinal)));

        await _fixture.StartAsync();

        Assert.False(_fixture.Body(HttpMethod.Post, StartPath).TryGetProperty("subtitle_track_index", out _));
    }

    [Fact]
    public async Task ThePlansSidecar_IsReadThroughTheProxy_OnceTheStreamPlays()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(ShowingTheSidecar()));
        var playback = await _fixture.StartAsync();
        Assert.Empty(_fixture.Engine.SubtitleFiles);

        _fixture.Engine.RaiseState(Frame(12.5));

        var route = _fixture.Proxy.Routes[^1];
        Assert.Equal(ScriptedSilo.Url(Sidecar0[1..]).OriginalString, route.Upstream.OriginalString);
        Assert.Equal(MediaRouteScope.File, route.Scope);
        Assert.Equal((route.LocalUrl, _fixture.Engine.LoadId), Assert.Single(_fixture.Engine.SubtitleFiles));
        Assert.Equal(3, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Equal(0, _fixture.Engine.SubtitleDelay);
        Assert.Equal("file:42:subtitle:0", playback.SubtitleTrack);
    }

    [Fact]
    public async Task AStreamFromTheServer_ShowsItsSidecarAtTheStreamsOwnTime()
    {
        // The transcode's stream starts at 598 s of the title; the cues are the title's time.
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        await _fixture.StartAsync();

        _fixture.Engine.RaiseState(Frame(2));

        Assert.Single(_fixture.Engine.SubtitleFiles);
        Assert.Equal(-598, _fixture.Engine.SubtitleDelay);
        Assert.Equal(3, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task ThePlansEmbeddedTrack_IsShownFromTheFile()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Showing(PlaybackPayloads.StartOpaqueIds, "render", 1,
            "\"embedded\": { \"stream_index\": 3 },")));
        await _fixture.StartAsync();

        _fixture.Engine.RaiseState(Frame(12.5));

        Assert.Empty(_fixture.Engine.SubtitleFiles);
        Assert.Equal(7, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task AnEmbeddedPick_InADirectPlay_ShowsTheFilesStream_AndIsSaved()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:2");
        await playback.StopAsync();

        Assert.Equal(8, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.Equal("file:42:subtitle:2", playback.SubtitleTrack);
        Assert.Equal("""
            {"subtitle_language":"jpn","subtitle_track_index":2,"subtitle_mode":"always","track_signature":{"source":"embedded","language":"jpn","codec":"pgs","label":"Japanese","forced":false,"hearing_impaired":false}}
            """, SavedChoice().GetRawText());
    }

    [Fact]
    public async Task ASidecarPick_IsReadThroughTheProxy()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:4");
        await Until(() => _fixture.Engine.SubtitleStreams.Contains(3), "the sidecar shows");

        Assert.EndsWith("/subtitles/4.vtt?file_id=42", _fixture.Proxy.Routes[^1].Upstream.OriginalString, StringComparison.Ordinal);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
    }

    [Fact]
    public async Task Off_HidesTheSubtitle_AndIsSavedAsOff()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(ShowingTheSidecar()));
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle(null);
        await playback.StopAsync();

        Assert.Equal(0, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Null(playback.SubtitleTrack);
        Assert.Equal("""{"subtitle_language":"","subtitle_track_index":-1,"subtitle_mode":"off"}""", SavedChoice().GetRawText());
    }

    [Fact]
    public async Task LeavingABurnIn_TakesANewPlan()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Showing(PlaybackPayloads.StartOpaqueIds, "burn_in", 3)));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        Assert.Equal(0, _fixture.Engine.SubtitleStreams[^1]);

        playback.SelectSubtitle(null);
        await playback.Recovery;

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("track_change", replan.GetProperty("operation").GetString());
        Assert.False(replan.GetProperty("selected_tracks").TryGetProperty("subtitle", out _));
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task APickInThePlayer_GoesWithTheNextReplan_AndANewAttempt()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:1");
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _fixture.Body(HttpMethod.Post, StartPath, 1).GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task APickInThePlayer_IsInTheReconnectsReplan()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        playback.SelectSubtitle("file:42:subtitle:4");

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;

        var subtitle = _fixture.Body(HttpMethod.Post, ReplanPath).GetProperty("selected_tracks").GetProperty("subtitle");
        Assert.Equal(("file:42:subtitle:4", 4), (subtitle.GetProperty("id").GetString(), subtitle.GetProperty("index").GetInt32()));
    }

    [Fact]
    public async Task ASidecarThatArrivesAfterAnotherLoad_IsNotShown()
    {
        var read = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Engine.ReadSubtitle = _ => read.Task;
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(ShowingTheSidecar()));
        var playback = await _fixture.PlayingAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;
        read.SetResult(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(3, _fixture.Engine.SubtitleStreams);
        Assert.True(_fixture.Proxy.Routes.Where(r => r.Upstream.OriginalString.Contains("/subtitles/", StringComparison.Ordinal)).All(r => r.Disposed));
    }

    [Fact]
    public async Task ASidecarThatCannotBeRead_IsNotLeftChecked()
    {
        _fixture.Engine.ReadSubtitle = _ => Task.FromResult<int?>(null);
        var playback = await _fixture.PlayingAsync();
        int changes = 0;
        playback.TracksChanged += (_, _) => changes++;

        playback.SelectSubtitle("file:42:subtitle:4");
        await Until(() => playback.SubtitleTrack is null, "the pick is taken back");

        Assert.Equal(2, changes);
        Assert.Equal(0, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task PickingASidecarAgain_WhileItIsRead_SharesTheRead()
    {
        var read = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Engine.ReadSubtitle = _ => read.Task;
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:4");
        playback.SelectSubtitle(null);
        playback.SelectSubtitle("file:42:subtitle:4");
        read.SetResult(3);
        await Until(() => _fixture.Engine.SubtitleStreams[^1] == 3, "the sidecar shows");

        Assert.Single(_fixture.Engine.SubtitleFiles);
    }

    [Fact]
    public async Task AnEmbeddedTrackTheFileListsOtherwise_IsReadAsItsSidecar()
    {
        // The file's list does not line up with the inventory: its first embedded track is not the forced signs.
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(new Regex(@"(""audio_tracks"":\s*\[[^\]]*\])").Replace(
            PlaybackPayloads.WatchStateOk, "$1, \"subtitle_tracks\": [ { \"index\": 3, \"language\": \"eng\", \"codec\": \"ass\" } ]", 1)));
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:1");
        await Until(() => _fixture.Engine.SubtitleFiles.Count == 1, "the sidecar is read");

        Assert.EndsWith("/subtitles/1.ass?file_id=42&embedded_stream_index=0", _fixture.Proxy.Routes[^1].Upstream.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain(7, _fixture.Engine.SubtitleStreams);
    }

    [Fact]
    public async Task ATrackNotInTheInventory_IsIgnored()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectSubtitle("file:42:subtitle:9");

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, SubtitlePrefsPath));
        Assert.DoesNotContain(_fixture.Engine.SubtitleStreams, stream => stream != 0);
    }

    // The plan of a replan after a subtitle was stored: the Spanish download is stored subtitle 77.
    private static string ListingStored(string naming) =>
        PlaybackPayloads.StartOpaqueIds.Replace("\"label\": \"Spanish (downloaded)\",", "\"label\": \"Spanish (downloaded)\", " + naming, StringComparison.Ordinal);

    [Fact]
    public async Task AnAddedSubtitle_TakesAPlanThatListsIt_AndShowsAsThePick()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ListingStored("\"sync_key\": \"stored-77\",")));
        var playback = await _fixture.PlayingAsync();

        playback.ShowAddedSubtitle("77");
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(30));
        await Until(() => _fixture.Engine.SubtitleFiles.Count == 1, "the sidecar is read");

        // No refresh exists: a track_change for the tracks that play brings the new inventory; the stream reloads.
        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("track_change", replan.GetProperty("operation").GetString());
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.Equal("file:42:subtitle:4", playback.SubtitleTrack);
        Assert.Equal("spa", SavedChoice().GetProperty("subtitle_language").GetString());
    }

    [Fact]
    public async Task AnOlderServersPlan_NamesTheStoredSubtitleInItsAddress()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ListingStored("").Replace("4.vtt?file_id=42", "4.vtt?file_id=42&downloaded_subtitle_id=77",
            StringComparison.Ordinal)));
        var playback = await _fixture.PlayingAsync();

        playback.ShowAddedSubtitle("77");
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(30));

        Assert.Equal("file:42:subtitle:4", playback.SubtitleTrack);
    }

    [Fact]
    public async Task APlanThatDoesNotListIt_IsAskedForOnce()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        string? before = playback.SubtitleTrack;

        playback.ShowAddedSubtitle("77");
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(30));
        await playback.Recovery;

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.Equal(before, playback.SubtitleTrack);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, SubtitlePrefsPath));
    }
}
