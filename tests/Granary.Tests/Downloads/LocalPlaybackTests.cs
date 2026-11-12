using Granary.Core.Api.Playback;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>A download played from this PC: the file, and what the player shows of it, with and without the server.</summary>
public sealed class LocalPlaybackTests : IAsyncDisposable
{
    private readonly LocalPlaybackFixture _fixture = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ADownloadedTitle_PlaysTheLocalFile_WithoutAServerSession()
    {
        var download = await _fixture.Harness.AddPlayableAsync();

        var playback = await _fixture.StartAsync();

        Assert.NotNull(playback);
        var load = Assert.Single(_fixture.Engine.Loaded);
        Assert.True(load.Url.IsFile);
        Assert.Equal(download.MediaPath, load.Url.LocalPath);
        Assert.Empty(load.Headers);
        // No subtitle of the file's own choosing, and the title plays however the one before was left.
        Assert.Equal(0, load.SubtitleStream);
        Assert.Equal([false], _fixture.Engine.PauseRequests);
        Assert.Null(playback.SessionId);
        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/playback/", StringComparison.Ordinal));
        Assert.All(_fixture.Silo.Sent, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(ServerPlaybackPhase.Preparing, playback.Status.Phase);

        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.True(playback.CanReachInPlace(600));
    }

    [Fact]
    public async Task AFolderWhoseNameAUriCouldMisread_StillPlaysItsFile()
    {
        // The downloads folder is the viewer's to choose.
        await _fixture.Harness.Preferences.SetFolderAsync(Path.Combine(_fixture.Harness.Directory, "My #1 shows 100%25 (new)"));
        var download = await _fixture.Harness.AddPlayableAsync();

        var playback = await _fixture.StartAsync();
        Assert.NotNull(playback);
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));
        playback.SelectSubtitle("local:sidecar:0");

        Assert.Equal(download.MediaPath, _fixture.Engine.Loaded[0].Url.LocalPath);
        Assert.Equal(Path.Combine(download.Folder, DownloadHarness.SubtitleFile), _fixture.Engine.SubtitleFiles[0].Url.LocalPath);
        Assert.Equal(Path.Combine(download.Folder, "poster.jpg"), playback.MediaInfo.Artwork!.LocalPath);
    }

    [Fact]
    public async Task TitleTracksChaptersAndMarkers_ComeFromTheManifest()
    {
        var playback = await _fixture.PlayingAsync();

        Assert.Equal("Example Series", playback.Title);
        Assert.Equal("S1 · E5 — Chapter Five", playback.Subtitle);
        Assert.Equal(7200, playback.DurationSeconds);
        Assert.Equal(0, playback.TimelineOffsetSeconds);
        Assert.Equal(new PlayingEpisode("ep_5", "sr_1", "Example Series", 1, 5), playback.Episode);
        Assert.Equal(2, playback.AudioTracks.Count);
        Assert.Equal(("Commentary", "en", "aac", 2, false), (playback.AudioTracks[1].Title, playback.AudioTracks[1].Language,
            playback.AudioTracks[1].Codec, playback.AudioTracks[1].Channels, playback.AudioTracks[1].Default));
        // selected_audio_track_index, as the server chose it for the download; mpv counts from 1.
        Assert.Equal(1, playback.AudioTrack);
        Assert.Equal(2, _fixture.Engine.Loaded[0].AudioStream);
        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null)], playback.Chapters);
        Assert.Equal([new PlaybackMarker(WatchMarkerKinds.Intro, 60, 90), new PlaybackMarker(WatchMarkerKinds.Credits, 7100, 7200)], playback.Markers);
        // Nothing of a server stream: no quality, no other version, no file to add subtitles to or edit markers of.
        Assert.Empty(playback.Qualities);
        Assert.Empty(playback.Versions);
        Assert.Null(playback.PlayingFileId);
        Assert.Null(playback.StreamInfo);
        Assert.Null(playback.Trickplay);
        Assert.Null(playback.SubtitleSync);
        await Assert.ThrowsAsync<NotSupportedException>(() => playback.SaveMarkersAsync(new Dictionary<string, WatchMarker?>(), LocalPlaybackFixture.Token));
    }

    [Fact]
    public async Task WindowsMediaControlsAndTheDiscordStatus_GetTheEpisode_WithThePictureOnThisPC()
    {
        var playback = await _fixture.PlayingAsync();

        var info = playback.MediaInfo;
        Assert.Equal(("Chapter Five", "Example Series — S1 · E5"), (info.Title, info.Subtitle));
        Assert.Equal(PlaybackMediaKind.Episode, info.Kind);
        Assert.Equal(("Example Series", 1, 5, "Chapter Five", "sr_1"), (info.SeriesTitle, info.SeasonNumber, info.EpisodeNumber, info.EpisodeTitle, info.CatalogId));
        Assert.True(info.Artwork!.IsFile);
        Assert.Equal(Path.Combine(_fixture.Harness.Find("dl_1")!.Folder, "poster.jpg"), info.Artwork.LocalPath);
    }

    [Fact]
    public async Task AnAudioPickFromTheTitlePage_PlaysThatTrack()
    {
        await _fixture.Harness.AddPlayableAsync();

        var playback = await _fixture.StartAsync(PlayRequest.Resume("ep_5", new PrePlayChoice("4567", IsVersionPicked: false, AudioTrack: 0)));

        Assert.Equal(1, _fixture.Engine.Loaded[0].AudioStream);
        Assert.Equal(0, playback!.AudioTrack);
    }

    [Fact]
    public async Task AnAudioPickInThePlayer_SwitchesInTheFile()
    {
        var playback = await _fixture.PlayingAsync();
        int changes = 0;
        playback.TracksChanged += (_, _) => changes++;

        playback.SelectAudio(0);

        Assert.Equal([1], _fixture.Engine.AudioSwitches);
        Assert.Equal(0, playback.AudioTrack);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ASidecarSubtitle_ShowsFromItsLocalFile()
    {
        var playback = await _fixture.PlayingAsync();
        var track = Assert.Single(playback.SubtitleTracks);
        Assert.Equal(("local:sidecar:0", SubtitleSources.External, "en", "srt"), (track.TrackId, track.Source, track.Language, track.Codec));
        // The server's default preference shows none.
        Assert.Null(playback.SubtitleTrack);
        Assert.Equal([0], _fixture.Engine.SubtitleStreams);

        playback.SelectSubtitle("local:sidecar:0");

        var file = Assert.Single(_fixture.Engine.SubtitleFiles);
        Assert.Equal(Path.Combine(_fixture.Harness.Find("dl_1")!.Folder, DownloadHarness.SubtitleFile), file.Url.LocalPath);
        Assert.Equal(_fixture.Engine.LoadId, file.LoadId);
        Assert.Equal(3, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Equal("local:sidecar:0", playback.SubtitleTrack);

        playback.SelectSubtitle(null);
        playback.SelectSubtitle("local:sidecar:0");

        // Off, and back on from the file read before.
        Assert.Equal([0, 3, 0, 3], _fixture.Engine.SubtitleStreams);
        Assert.Single(_fixture.Engine.SubtitleFiles);
    }

    [Fact]
    public async Task ASubtitleFileThatNeverArrived_IsNotOffered()
    {
        var download = await _fixture.Harness.AddPlayableAsync();
        File.Delete(Path.Combine(download.Folder, DownloadHarness.SubtitleFile));

        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Empty(playback!.SubtitleTracks);
    }

    [Fact]
    public async Task ASubtitleThePlayerCannotRead_ShowsNone()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.ReadSubtitle = _ => Task.FromResult<int?>(null);

        playback.SelectSubtitle("local:sidecar:0");

        Assert.Null(playback.SubtitleTrack);
        Assert.Equal(0, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task TheFilesOwnSubtitles_AreListedOnceItPlays()
    {
        _fixture.FileHasSubtitles();
        await _fixture.Harness.AddPlayableAsync();
        var playback = await _fixture.StartAsync();
        Assert.Equal(["local:sidecar:0"], playback!.SubtitleTracks.Select(t => t.TrackId));

        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Equal(["local:sidecar:0", "local:stream:1", "local:stream:2"], playback.SubtitleTracks.Select(t => t.TrackId));
        var german = playback.SubtitleTracks[2];
        Assert.Equal((SubtitleSources.Embedded, "ger", "Deutsch", false), (german.Source, german.Language, german.Label, german.Forced));

        playback.SelectSubtitle("local:stream:2");

        Assert.Equal(2, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Empty(_fixture.Engine.SubtitleFiles);
    }

    [Fact]
    public async Task ASubtitlePickFromTheTitlePage_ShowsThatTrack()
    {
        _fixture.FileHasSubtitles();
        await _fixture.Harness.AddPlayableAsync();

        // The server lists the external English first (0), then the file's forced English (1) and its German (2).
        var playback = await _fixture.StartAsync(PlayRequest.Resume("ep_5", new PrePlayChoice("4567", IsVersionPicked: false, SubtitleTrack: 2)));
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Equal("local:stream:2", playback!.SubtitleTrack);
        Assert.Equal(2, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task OffPickedOnTheTitlePage_ShowsNone_WhateverTheProfilePrefers()
    {
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            LocalPlaybackFixture.Watch(preferences: """, "effective_subtitle_language": "en", "effective_subtitle_mode": "always" """)));
        await _fixture.Harness.AddPlayableAsync();

        var playback = await _fixture.StartAsync(PlayRequest.Resume("ep_5", new PrePlayChoice("4567", IsVersionPicked: false, SubtitleTrack: -1)));
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Null(playback!.SubtitleTrack);
        Assert.Empty(_fixture.Engine.SubtitleFiles);
    }

    [Fact]
    public async Task TheProfilesSubtitleLanguage_PicksTheMatchingSidecar()
    {
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            LocalPlaybackFixture.Watch(preferences: """, "effective_subtitle_language": "en", "effective_subtitle_mode": "always" """)));

        var playback = await _fixture.PlayingAsync();

        Assert.Equal("local:sidecar:0", playback.SubtitleTrack);
        Assert.Single(_fixture.Engine.SubtitleFiles);
        Assert.Equal(3, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task APickMadeBeforeTheFilePlays_StandsOverTheStartsOwnChoice()
    {
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            LocalPlaybackFixture.Watch(preferences: """, "effective_subtitle_language": "en", "effective_subtitle_mode": "always" """)));
        await _fixture.Harness.AddPlayableAsync();
        var playback = await _fixture.StartAsync();

        playback!.SelectSubtitle(null);
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Null(playback.SubtitleTrack);
        Assert.Empty(_fixture.Engine.SubtitleFiles);
    }

    [Fact]
    public async Task Resume_StartsWhereTheServerSaysTheProfileLeftIt()
    {
        await _fixture.Harness.AddPlayableAsync();

        await _fixture.StartAsync();

        // Rewind on resume is none unless Settings → Playback → This PC sets one.
        Assert.Equal(TimeSpan.FromSeconds(1830.5), _fixture.Engine.Loaded[0].Start);
    }

    [Fact]
    public async Task Resume_GoesBackByThisPCsRewindOnResume_ButNotForAChangeOfEpisode()
    {
        _fixture.Device.ResumeRewindSeconds = 7;
        await _fixture.Harness.AddPlayableAsync();

        await _fixture.StartAsync();
        await _fixture.StartAsync(PlayRequest.ResumeWithoutRewind("ep_5"));
        await _fixture.StartAsync(PlayRequest.FromStart("ep_5"));

        Assert.Equal([1823.5, 1830.5, 0], _fixture.Engine.Loaded.Select(l => l.Start!.Value.TotalSeconds));
    }

    [Fact]
    public async Task WithoutTheServer_ResumeStartsWhereThisPCLeftIt()
    {
        _fixture.GoOffline();
        await _fixture.Harness.AddPlayableAsync(change: r => r with { ResumeMs = 600_000, ResumeAt = Fakes.ManualTimeProvider.DefaultStart });

        await _fixture.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(600), _fixture.Engine.Loaded[0].Start);
    }

    [Fact]
    public async Task AResumePointAtTheVeryEnd_StartsOver()
    {
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, LocalPlaybackFixture.Watch(7198)));
        await _fixture.Harness.AddPlayableAsync();

        await _fixture.StartAsync();

        Assert.Equal(TimeSpan.Zero, _fixture.Engine.Loaded[0].Start);
    }

    [Fact]
    public async Task AFileThatWillNotPlay_FailsWithRetry_AndNothingStreams()
    {
        await _fixture.Harness.AddPlayableAsync();
        var playback = await _fixture.StartAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "unrecognized file format"));

        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "This download can't be played. Try again, or delete it and download it again.", CanRetry: true),
            playback!.Status);
        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/playback/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheEndOfTheFile_EndsTheTitle()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(7195));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        Assert.Equal(ServerPlaybackPhase.Ended, playback.Status.Phase);
    }

    [Fact]
    public async Task AnotherLoadsEnd_IsNotThisTitles()
    {
        var playback = await _fixture.PlayingAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "old", LoadId: 99));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Stopped, null));

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
    }

    [Fact]
    public async Task WithoutTheServer_TheProfilesSettingsAreTheDefaults_AndTheTitleStillPlays()
    {
        _fixture.GoOffline();

        var playback = await _fixture.PlayingAsync();

        Assert.Equal(PlaybackPreferences.Default, playback.Preferences);
        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal(1, _fixture.Engine.Speed);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswer_HoldsTheStartForThreeSecondsAtMost()
    {
        _fixture.Silo.OnCancellable(HttpMethod.Get, LocalPlaybackFixture.SettingsPath, Never);
        _fixture.Silo.OnCancellable(HttpMethod.Get, LocalPlaybackFixture.WatchPath, Never);
        await _fixture.Harness.AddPlayableAsync(change: r => r with { ResumeMs = 600_000 });
        var playback = (LocalPlayback)(await _fixture.Playbacks.TryCreateAsync(PlayRequest.Resume("ep_5"), LocalPlaybackFixture.Token))!;

        var start = playback.StartAsync(PlayRequest.Resume("ep_5"), LocalPlaybackFixture.Token);
        Assert.Empty(_fixture.Engine.Loaded);
        _fixture.Harness.Time.Advance(LocalPlayback.PreferencesBudget);
        await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);

        Assert.Equal(TimeSpan.FromSeconds(600), Assert.Single(_fixture.Engine.Loaded).Start);
        Assert.Equal(PlaybackPreferences.Default, playback.Preferences);

        static async Task<HttpResponseMessage> Never(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task TheProfilesSettings_ApplyAsForAStream()
    {
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.SettingsPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, """
            { "items": [
                { "key": "player.playback_speed", "value": 1.25, "source": "profile_device" },
                { "key": "player.subtitle_sync_ms", "value": 300, "source": "profile" },
                { "key": "player.audio_sync_ms", "value": 150, "source": "profile_device" },
                { "key": "player.video_skip_back_seconds", "value": 15, "source": "profile" } ], "revision": 2 }
            """));

        var playback = await _fixture.PlayingAsync();

        Assert.Equal(1.25, playback.Speed);
        Assert.Equal(1.25, _fixture.Engine.Speed);
        Assert.Equal(300, playback.SubtitleDelayMs);
        Assert.Equal(0.3, _fixture.Engine.SubtitleDelay, 3);
        Assert.Equal(0.15, _fixture.Engine.AudioDelay, 3);
        Assert.Equal(15, playback.Preferences.SkipBackSeconds);
        Assert.NotNull(_fixture.Engine.SubtitleLook);
    }

    [Fact]
    public async Task TheAudioDelay_FollowsAChangeWhileTheTitlePlays_AndNoLongerAfterItsStop()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Silo.On(HttpMethod.Put, "api/v2/settings/values/player.audio_sync_ms", ScriptedSilo.NoContent());

        _fixture.AudioDelay.Set(200);
        Assert.Equal(0.2, _fixture.Engine.AudioDelay, 3);

        await playback.StopAsync();
        _fixture.AudioDelay.Set(-100);

        Assert.Equal(0.2, _fixture.Engine.AudioDelay, 3);
    }

    [Fact]
    public async Task ASpeedTheViewerSets_PlaysAtOnce_AndIsSavedForThisDevice_AsTheDownloadsProfile()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SetSpeed(1.5);
        Assert.Equal(1.5, _fixture.Engine.Speed);
        await playback.StopAsync();

        var save = Assert.Single(_fixture.Silo.SentTo(HttpMethod.Put, "api/v2/settings/values/player.playback_speed"));
        Assert.Contains("1.5", save.Body, StringComparison.Ordinal);
        Assert.Contains("scope=profile_device", save.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("7", save.AsProfile);
    }

    [Fact]
    public async Task WithoutTheServer_ASettingTheViewerChanges_HoldsForTheTitle()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync();
        _fixture.Silo.On(HttpMethod.Put, "api/v2/settings/values/playback.auto_play_next", ScriptedSilo.NetworkError());

        playback.SetAutoPlayNext(false);
        playback.SetSleepTimerDefault(45);
        playback.SetSubtitleDelay(-200);
        await playback.StopAsync();

        Assert.False(playback.Preferences.Markers.AutoPlayNext);
        Assert.Equal(45, playback.Preferences.SleepTimerMinutes);
        Assert.Equal(-0.2, _fixture.Engine.SubtitleDelay, 3);
    }

    [Fact]
    public async Task ASeek_MovesInTheFile()
    {
        var playback = await _fixture.PlayingAsync();

        playback.Seek(900);

        Assert.Equal([TimeSpan.FromSeconds(900)], _fixture.Engine.Seeks);
    }

    [Fact]
    public async Task AfterItsStop_ThePlaybackLeavesTheEngineToTheNextTitle()
    {
        var playback = await _fixture.PlayingAsync();

        await playback.StopAsync();
        await playback.DisposeAsync();
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "the next title's"));

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.False(_fixture.Engine.HasSubscribers);
    }

    [Fact]
    public async Task ADownload_CountsAsPlaying_FromItsPlaybackUntilThatIsLetGo()
    {
        var playback = await _fixture.PlayingAsync();

        Assert.True(_fixture.Harness.Playing.Contains(DownloadHarness.Scope7, "dl_1"));
        await playback.StopAsync();
        // Stopped, the player may still have the file open: it counts until the playback is let go.
        Assert.True(_fixture.Harness.Playing.Contains(DownloadHarness.Scope7, "dl_1"));

        await playback.DisposeAsync();

        Assert.False(_fixture.Harness.Playing.Contains(DownloadHarness.Scope7, "dl_1"));
    }

    [Fact]
    public async Task WithoutTheServer_ADownloadStartsWithTheSettingsAndTheSubtitleItHadWhenTheServerLastSaid()
    {
        // Played once with the server: the profile watches at 1.5x with large yellow subtitles, always on, in English.
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.SettingsPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, """
            { "items": [
                { "key": "player.playback_speed", "value": 1.5, "source": "profile_device" },
                { "key": "playback.subtitle_appearance", "value": { "fontSize": "xlarge", "fontColor": "#ffff00" }, "source": "profile" },
                { "key": "player.subtitle_sync_ms", "value": 250, "source": "profile" }
              ], "revision": 3 }
            """));
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            LocalPlaybackFixture.Watch(preferences: """, "effective_subtitle_language": "en", "effective_subtitle_mode": "always" """)));
        var online = await _fixture.PlayingAsync();
        await online.StopAsync();
        await online.DisposeAsync();
        Assert.Equal(new DownloadSubtitlePreference("en", "always", true, null), _fixture.Harness.Find("dl_1")!.Record.SubtitlePreference);

        _fixture.GoOffline();
        var playback = await _fixture.StartAsync(PlayRequest.FromStart("ep_5"));
        _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(0));

        Assert.Equal((1.5, 250), (playback!.Speed, playback.SubtitleDelayMs));
        Assert.Equal((1.5, 68d, "#ffff00"), (_fixture.Engine.Speed, _fixture.Engine.SubtitleLook!.FontSize, _fixture.Engine.SubtitleLook.FontColor));
        // The English subtitle kept beside the media shows, as with the server.
        Assert.Equal("local:sidecar:0", playback.SubtitleTrack);
    }

    [Fact]
    public async Task ADownloadNeverPlayedWithTheServer_StartsWithTheDefaults_WithoutIt()
    {
        _fixture.GoOffline();

        var playback = await _fixture.PlayingAsync();

        Assert.Equal((1d, 0), (playback.Speed, playback.SubtitleDelayMs));
        Assert.Equal(SubtitleAppearance.Default, _fixture.Engine.SubtitleLook);
        Assert.Null(playback.SubtitleTrack);
    }

    [Fact]
    public async Task ThisDevicesOwnSubtitleDelay_IsClearedAgain_WhenItsFirstClearDidNotGoThrough()
    {
        const string key = "api/v2/settings/values/player.subtitle_sync_ms";
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.SettingsPath, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, """
            { "items": [ { "key": "player.subtitle_sync_ms", "value": 300, "source": "profile_device" } ], "revision": 2 }
            """));
        _fixture.Silo.On(HttpMethod.Put, key, ScriptedSilo.NoContent());
        // The first clear fails; the device's own value still outranks the profile's that was just saved.
        _fixture.Silo.On(HttpMethod.Delete, key, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.NoContent());
        var playback = await _fixture.PlayingAsync();

        playback.SetSubtitleDelay(500);
        _fixture.Harness.Time.Advance(TimeSpan.FromSeconds(1));
        playback.SetSubtitleDelay(700);
        _fixture.Harness.Time.Advance(TimeSpan.FromSeconds(1));
        playback.SetSubtitleDelay(900);
        await playback.StopAsync();

        // The second save clears again, and once that went through the third leaves it be.
        Assert.Equal(3, _fixture.Silo.SentTo(HttpMethod.Put, key).Count);
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Delete, key).Count);
    }
}
