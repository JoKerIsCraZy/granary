using System.Net;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;
using static Granary.Tests.Playback.SubtitleSyncTrackerTests;

namespace Granary.Tests.ViewModels;

/// <summary>The subtitle menu's timing lines and TIMING section, and the sync indicator, as Silo's web player shows them.</summary>
public sealed class PlayerViewModelSubtitleSyncTests : IDisposable
{
    private const string Key = "stored-12";
    private const string Track = "file:41:subtitle:3";
    private const string ListPath = "api/v2/subtitles/41/sync";
    private const string KeyPath = ListPath + "/" + Key;

    private static readonly SubtitleInventoryItem German = new() { TrackId = Track, Source = "downloaded", Language = "de", SyncKey = Key };

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SubtitleSyncTracker _tracker;
    private readonly List<FakeServerPlayback> _playbacks = [];

    public PlayerViewModelSubtitleSyncTests()
    {
        _tracker = new SubtitleSyncTracker(new SubtitleSyncApi(_silo.Http), new ImmediateDispatcher(), _time, new LogSink().For<SubtitleSyncTracker>());
        _silo.On(HttpMethod.Get, "api/v2/subtitles/sync/status", Ok(Status()));
        _silo.On(HttpMethod.Get, ListPath, Ok(List(State(Key, Job("j0", "synced", result: Timing(-3000)), Timing(-3000)))));
    }

    public void Dispose() => _tracker.Dispose();

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> ShowingGermanAsync()
    {
        var viewModel = new PlayerViewModel(new FakePlaybackEngine(), new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { SubtitleSync = _tracker };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        _tracker.Follow(ScriptedSilo.Base, "41", [Key]);
        await Until(() => _tracker.EntryOf(Key) is not null, "the sync state was read");
        Playback.SubtitleSyncKey = Key;
        Playback.RaiseSubtitles([German], Track);
        return viewModel;
    }

    [Fact]
    public async Task ATracksRow_SaysHowItsTimingWasCorrected()
    {
        var viewModel = await ShowingGermanAsync();

        var row = viewModel.SubtitleTracks.Single(r => r.TrackId == Track);
        Assert.Equal("Synced −3.0 s", row.SyncStatus);
        Assert.Contains("Synced −3.0 s", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTimingSection_IsForTheSubtitleThatShows()
    {
        var viewModel = await ShowingGermanAsync();

        Assert.True(viewModel.SubtitleTiming.IsVisible);
        Assert.True(viewModel.SubtitleTiming.ShowsSync);
        Assert.True(viewModel.SubtitleTiming.ShowsReset);
        Assert.Equal("Synced to the audio: −3.0 s", viewModel.SubtitleTiming.ResultText);

        Playback.SubtitleSyncKey = null;
        Playback.RaiseSubtitles([German], selected: null);

        Assert.False(viewModel.SubtitleTiming.IsVisible);
    }

    [Fact]
    public async Task SyncToAudio_IsFollowedOnScreen_UntilTheNewTimingShows()
    {
        var viewModel = await ShowingGermanAsync();
        _silo.On(HttpMethod.Post, KeyPath, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Key, Job("j1", "running", 0.25, "analyzing"), Timing(-3000)))));

        await viewModel.SyncSubtitleToAudioCommand.ExecuteAsync(null);

        Assert.Single(_silo.SentTo(HttpMethod.Post, KeyPath));
        Assert.Equal("Syncing German subtitles", viewModel.SyncNoticeTitle);
        Assert.True(viewModel.IsSyncNoticeRunning);
        Assert.Equal(25, viewModel.SyncNoticePercent);
        Assert.True(viewModel.SubtitleTiming.IsRunning);
        Assert.False(viewModel.SubtitleTiming.AreActionsEnabled);

        var synced = new SubtitleSyncJob { Id = "j1", Status = "synced", Trigger = "manual", Result = new() { OffsetMs = -1000, RawScale = 1 } };
        _tracker.OnSyncUpdated(Key, new SubtitleTiming { OffsetMs = -1000, RawScale = 1 }, synced);
        Assert.Equal("Applying new timing…", viewModel.SyncNoticeTitle);
        Playback.RaiseSubtitleTimingChanged(Key);
        Playback.RaiseSubtitleCuesReloaded(Key);

        Assert.Equal("Subtitles synced", viewModel.SyncNoticeTitle);
        Assert.Equal("−1.0 s", viewModel.SyncNoticeDetail);
        Assert.Equal(SubtitleSyncTone.Success, viewModel.SyncNoticeTone);
        Assert.Equal("Synced −1.0 s", viewModel.SubtitleTracks.Single(r => r.TrackId == Track).SyncStatus);
        viewModel.DismissSyncNoticeCommand.Execute(null);
        Assert.False(viewModel.HasSyncNotice);
    }

    [Fact]
    public async Task ASecondPress_WhileASyncIsAsked_IsIgnored()
    {
        var viewModel = await ShowingGermanAsync();
        var gate = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, KeyPath, After(gate.Task, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Key, Job("j1", "pending"))))));

        var first = viewModel.SyncSubtitleToAudioCommand.ExecuteAsync(null);
        Assert.False(viewModel.SyncSubtitleToAudioCommand.CanExecute(null));
        viewModel.SyncSubtitleToAudioCommand.Execute(null);
        gate.SetResult();
        await first;

        Assert.Single(_silo.SentTo(HttpMethod.Post, KeyPath));
    }

    [Fact]
    public async Task OpeningTheMenu_ReadsTheSubtitlesAgain()
    {
        var viewModel = await ShowingGermanAsync();

        viewModel.SubtitleMenuOpened();

        await Until(() => _silo.SentTo(HttpMethod.Get, ListPath).Count == 2, "the list was read again");
    }

    [Fact]
    public async Task LeavingThePlayer_PutsTheTimingAway()
    {
        var viewModel = await ShowingGermanAsync();

        viewModel.Leave();

        Assert.False(viewModel.SubtitleTiming.IsVisible);
        Assert.False(viewModel.HasSyncNotice);
    }
}
