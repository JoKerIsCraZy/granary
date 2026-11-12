using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>
/// The player with downloads: a title on this PC plays from it, and what follows the player (Windows' media controls, the
/// Discord status, the audio delay, the sleep timer) is fed as for a stream.
/// </summary>
public sealed class PlayerDownloadsTests : IAsyncDisposable
{
    private readonly LocalPlaybackFixture _fixture = new();
    private readonly List<FakeServerPlayback> _streams = [];
    private readonly RecordingNavigation _navigation = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // Runs as a stream's playback is made, before the player has taken it.
    private Action? _whileAStreamIsMade;

    private PlayerViewModel NewPlayer(IUpNextSource? upNext = null) =>
        new(_fixture.Engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var stream = new FakeServerPlayback();
            _streams.Add(stream);
            _whileAStreamIsMade?.Invoke();
            return stream;
        }, _fixture.Stops, _fixture.Harness.Time, upNext, audioDelay: _fixture.AudioDelay, devicePreferences: _fixture.Device,
            downloads: _fixture.Playbacks);

    private void Frame(double seconds, bool paused = false) => _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(seconds, paused));

    [Fact]
    public async Task ADownloadedTitle_PlaysFromThisPC_AndAnyOtherStreams()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();

        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);

        Assert.Empty(_streams);
        Assert.True(_fixture.Engine.Loaded[0].Url.IsFile);
        Assert.Equal(PlayerPhase.Playing, player.Phase);
        Assert.Equal(("Example Series", "S1 · E5 — Chapter Five"), (player.Title, player.Subtitle));
        Assert.Equal(7200, player.DurationSeconds);
        Assert.True(player.CanSeek);
        Assert.Equal(2, player.AudioTracks.Count);
        Assert.True(player.HasSubtitleMenu);
        Assert.Equal(["Off", "English"], player.SubtitleTracks.Select(t => t.Title));
        Assert.Equal(2, player.Markers.Count);
        // A download has no file on the server to add subtitles to or to edit the markers of.
        Assert.Null(player.SubtitleFileId);
        Assert.False(player.CanEditMarkers);

        await player.StartAsync(PlayRequest.Resume("mv_streamed"));

        Assert.Single(_streams);
        Assert.Equal(PlayRequest.Resume("mv_streamed"), _streams[0].Started);
    }

    [Fact]
    public async Task LeavingADownload_TellsThePagesOnce_WhenTheServerHasWhereTheViewerLeftOff()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        int told = 0;
        int sentWhenTold = -1;
        _fixture.Changes.Changed += (_, _) =>
        {
            Volatile.Write(ref sentWhenTold, _fixture.Silo.SentTo(HttpMethod.Post, LocalPlaybackFixture.SyncPath).Count);
            Interlocked.Increment(ref told);
        };
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);
        Frame(600);

        player.Leave();
        Assert.True(await _fixture.Stops.WhenStoppedAsync(TimeSpan.FromSeconds(5)));
        // The flush the last record asked for finds nothing more to send.
        await _fixture.FlushedAsync();

        // Home's Continue Watching and the title's page load again at the stop, as after a stream, and read the new position:
        // the stop waited for the server to take it. The send itself told nobody a second time.
        Assert.Equal((1, 1), (Volatile.Read(ref told), Volatile.Read(ref sentWhenTold)));
        Assert.Contains("\"position_ms\":600000", _fixture.Silo.SentTo(HttpMethod.Post, LocalPlaybackFixture.SyncPath)[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingADownloadWithoutTheServer_TellsThePagesAtTheStop_AndAgainOnceThePositionArrived()
    {
        _fixture.GoOffline();
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        int told = 0;
        _fixture.Changes.Changed += (_, _) => Interlocked.Increment(ref told);
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);
        Frame(600);

        player.Leave();
        Assert.True(await _fixture.Stops.WhenStoppedAsync(TimeSpan.FromSeconds(5)));
        await _fixture.FlushedAsync();
        Assert.Equal(1, Volatile.Read(ref told));
        Assert.Single(_fixture.Queue.Entries(DownloadHarness.Scope7));

        // The connection is back: the position goes, and the pages hear that the server has it now.
        _fixture.Silo.On(HttpMethod.Post, LocalPlaybackFixture.SyncPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            """{ "items": [ { "index": 0, "media_item_id": "ep_5", "status": "success" } ], "summary": { "total": 1, "succeeded": 1, "failed": 0 } }"""));
        await _fixture.Sync.FlushAsync(readBack: false, LocalPlaybackFixture.Token);

        Assert.Equal(2, Volatile.Read(ref told));
        Assert.Empty(_fixture.Queue.Entries(DownloadHarness.Scope7));
    }

    [Fact]
    public async Task WindowsMediaControls_ShowTheDownload_WithItsPictureOnThisPC()
    {
        _fixture.GoOffline();
        var download = await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        var controls = new FakeSystemMediaControls();
        using var media = new PlayerSystemMedia(player, controls, new ImmediateDispatcher(), _fixture.Harness.Time);

        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(30);

        var shown = controls.Current!;
        Assert.Equal(("Chapter Five", "Example Series — S1 · E5", SystemMediaStatus.Playing, 30d, 7200d),
            (shown.Title, shown.Subtitle, shown.Status, shown.PositionSeconds, shown.DurationSeconds));
        Assert.Equal(Path.Combine(download.Folder, "poster.jpg"), shown.Artwork!.LocalPath);
        Assert.True(shown.CanPlayPause);
        Assert.True(shown.CanSeek);
    }

    [Fact]
    public async Task TheDiscordStatus_ShowsTheDownload_AlsoWithoutAConnection()
    {
        _fixture.GoOffline();
        _fixture.Silo.On(HttpMethod.Get, "api/v2/catalog/items/sr_1", ScriptedSilo.NetworkError());
        _fixture.Device.DiscordStatus = true;
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        var presence = new FakeRichPresence();
        var titles = new TmdbTitleLookup(new CatalogApi(_fixture.Silo.Http), new RequestsApi(_fixture.Silo.Http), _fixture.Harness.Session,
            _fixture.Harness.Logs.For<TmdbTitleLookup>());
        using var status = new PlayerRichPresence(player, presence, _fixture.Device, titles, new ImmediateDispatcher(), _fixture.Harness.Time);

        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(30);
        await status.Lookup;

        var shown = presence.Current!;
        Assert.Equal(("Example Series", "S01E05 · Chapter Five"), (shown.Details, shown.State));
        // The poster and the page come from TMDB through the server: none without it.
        Assert.Null(shown.Poster);
    }

    [Fact]
    public async Task TheAudioDelay_StepsForADownload_AsForAStream()
    {
        _fixture.Silo.On(HttpMethod.Put, "api/v2/settings/values/player.audio_sync_ms", ScriptedSilo.NoContent());
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);

        player.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);

        Assert.Equal(50, player.AudioDelayMs);
        Assert.Equal(0.05, _fixture.Engine.AudioDelay, 3);
        Assert.True(player.ShowsAudioDelay);
    }

    [Fact]
    public async Task TheKeptVolume_AndTheSleepTimersDuration_HoldForADownload()
    {
        _fixture.Device.Volume = new PlayerVolume(40, IsMuted: false);
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.SettingsPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, """
            { "items": [ { "key": "player.sleep_timer_default_minutes", "value": 45, "source": "profile_device" } ], "revision": 2 }
            """));
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();

        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);

        Assert.Contains(40, _fixture.Engine.VolumeRequests);
        Assert.Contains(player.SleepTimerOptions, option => option is { Minutes: 45, IsDefault: true });
    }

    [Fact]
    public async Task TheEngineStillPlayingTheTitleBefore_DoesNotCountAsTheDownload_WhileItsFolderIsRead()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        using var player = NewPlayer();
        await player.StartAsync(PlayRequest.Resume("mv_streamed"));
        _streams[0].Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Frame(100);
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };

        var start = player.StartAsync(PlayRequest.FromStart("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        // The stream before has stopped for the player, but the engine still reports it.
        Frame(110);

        Assert.Equal(PlayerPhase.Preparing, player.Phase);
        Assert.False(player.CanSeek);
        held.Set();
        await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);
        Assert.True(_fixture.Engine.Loaded[^1].Url.IsFile);
    }

    [Fact]
    public async Task AStartThatWaitedForTheFolder_GivesWayToTheViewerLeaving()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        using var player = NewPlayer();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };

        var start = player.StartAsync(PlayRequest.FromStart("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        player.Leave();
        held.Set();
        await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);

        Assert.Empty(_fixture.Engine.Loaded);
        Assert.Empty(_streams);
        Assert.True(player.HasLeft);
        // The download's playback was made and let go, never started.
        Assert.Empty(_fixture.Statuses);
    }

    [Fact]
    public async Task WithoutTheServer_TheNextDownloadedEpisode_PlaysFromThisPCToo()
    {
        _fixture.GoOffline();
        foreach (string path in new[] { "api/v2/catalog/series/sr_1/seasons/1/episodes", "api/v2/catalog/series/sr_1/seasons", "api/v2/watch/ep_7" })
        {
            _fixture.Silo.On(HttpMethod.Get, path, ScriptedSilo.NetworkError());
        }
        await _fixture.Harness.AddPlayableAsync();
        var next = await _fixture.Harness.AddPlayableAsync("dl_7", "ep_7", episode: 7, change: r => r with { Title = "Chapter Seven" });
        var upNext = new DownloadUpNext(
            new UpNextSource(new CatalogApi(_fixture.Silo.Http), _fixture.Harness.Session, _fixture.Harness.Logs.For<UpNextSource>()),
            _fixture.Harness.Store, _fixture.Harness.Session, _fixture.Harness.Time);
        using var player = NewPlayer(upNext);
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);
        await DownloadHarness.Until(() => player.HasNextEpisode, "the next episode is known");

        await player.PlayNextEpisodeCommand.ExecuteAsync(null);

        Assert.Equal(next.MediaPath, _fixture.Engine.Loaded[^1].Url.LocalPath);
        Assert.Empty(_streams);
        // It is the seventh episode that plays, by what the player says of it too.
        Frame(0);
        Assert.Equal(("Example Series", "S1 · E7 — Chapter Seven"), (player.Title, player.Subtitle));
    }

    [Fact]
    public async Task AFilePlayedDirectly_WhileADownloadsFolderIsStillRead_CountsAsPlaying()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        using var player = NewPlayer();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };
        var first = player.StartAsync(PlayRequest.FromStart("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv"));
        Frame(1);

        Assert.Equal(PlayerPhase.Playing, player.Phase);
        held.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);
        Assert.Equal(PlayerPhase.Playing, player.Phase);
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task APlayerThatGoesWhileADownloadsFolderIsRead_StartsNothing()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        var player = NewPlayer();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };

        var start = player.StartAsync(PlayRequest.FromStart("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        player.Dispose();
        held.Set();
        await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);

        Assert.Empty(_fixture.Engine.Loaded);
        Assert.Empty(_streams);
        // Neither the page's player nor the playback it never started listens to the engine any more.
        Assert.False(_fixture.Engine.HasSubscribers);
    }

    [Fact]
    public async Task Retry_AfterAFileThatWouldNotPlay_TriesTheFileAgain_WithANewPlayback()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "unrecognized file format"));
        Assert.Equal((PlayerPhase.Failed, LocalPlayback.CannotPlay, true), (player.Phase, player.ErrorMessage, player.CanRetry));

        await player.RetryCommand.ExecuteAsync(null);
        Frame(0);

        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.All(_fixture.Engine.Loaded, load => Assert.True(load.Url.IsFile));
        Assert.Equal(PlayerPhase.Playing, player.Phase);
        Assert.Empty(_streams);
        // The playback that failed is let go: a change of the audio delay reaches the engine once, through the new one.
        _fixture.Silo.On(HttpMethod.Put, "api/v2/settings/values/player.audio_sync_ms", ScriptedSilo.NoContent());
        player.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);
        Assert.Equal(0.05, _fixture.Engine.AudioDelay, 3);
    }

    [Fact]
    public async Task AStartThatWaitedForTheFolder_GivesWayToANewerStart()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        using var player = NewPlayer();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };

        var first = player.StartAsync(PlayRequest.FromStart("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        _fixture.Playbacks.Reading = null;
        await player.StartAsync(PlayRequest.Resume("mv_streamed"));
        held.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);

        // Only the newer title plays.
        Assert.Single(_streams);
        Assert.Empty(_fixture.Engine.Loaded);
        Assert.Empty(_fixture.Statuses);
    }

    [Fact]
    public async Task ThePlaybackInfo_SaysTheDownloadIsALocalFile_WithTheAudioTrackThatPlays()
    {
        _fixture.GoOffline();
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);

        player.TogglePlaybackInfoCommand.Execute(null);

        var rows = player.PlaybackInfoSections.ToDictionary(s => s.Title, s => s.Rows.ToDictionary(r => r.Label, r => r.Value));
        Assert.Equal(PlaybackInfo.LocalFile, rows["Player"]["Play method"]);
        // The manifest's facts; the commentary is the track the download starts with.
        Assert.Equal(("mkv", "H.264", "SDR", "AAC", "2"), (rows["Current Source File"]["Container"], rows["Current Source File"]["Video codec"],
            rows["Current Source File"]["Video range type"], rows["Current Source File"]["Audio codec"], rows["Current Source File"]["Audio channels"]));
    }

    [Fact]
    public async Task WhatTheEngineSaysBeforeTheNewPlaybackIsThePlayers_IsNotTakenForTheNewTitle()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var player = NewPlayer();
        await player.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(100);
        Assert.Equal(PlayerPhase.Playing, player.Phase);

        // The next title streams. Between the look for its download and the player taking its playback, the engine still
        // reports the title before: a frame, then its end.
        _whileAStreamIsMade = () =>
        {
            Frame(110);
            _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        };
        await player.StartAsync(PlayRequest.Resume("mv_streamed"));

        // Neither is the new title's: it is still being prepared, cannot be sought in, and has not ended.
        Assert.Equal(PlayerPhase.Preparing, player.Phase);
        Assert.False(player.CanSeek);
        Assert.Single(_streams);
    }
}
