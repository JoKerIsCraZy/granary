using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The player's bar as Settings → Playback → Player buttons has it.</summary>
public sealed class PlayerViewModelButtonsTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryPlayerButtons _buttons = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel Create(IPlayerButtonsLayout? buttons) => new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
    {
        var playback = new FakeServerPlayback { DurationSeconds = 2700 };
        _playbacks.Add(playback);
        return playback;
    }, new PlaybackStops(), _time, buttons: buttons);

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = Create(_buttons);
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    [Fact]
    public void WithoutALayout_EveryButtonShows()
    {
        using var viewModel = Create(null);

        Assert.Equal(PlayerBar.Default, viewModel.Bar);
        Assert.True(viewModel.ShowsSleepTimer);
    }

    [Fact]
    public async Task ThePlayer_StartsWithTheProfilesBar_AndFollowsIt()
    {
        _buttons.Set(PlayerBar.Default.WithMoved(PlayerButton.FullScreen, -1).WithShown(PlayerButton.Chapters, false));
        var viewModel = await PlayingAsync();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(_buttons.Current, viewModel.Bar);
        _buttons.SetShown(PlayerButton.Chapters, true);

        Assert.True(viewModel.Bar.Chapters);
        Assert.Equal(PlayerButton.FullScreen, viewModel.Bar.Order[^2]);
        Assert.Contains(nameof(PlayerViewModel.Bar), changed);
    }

    [Fact]
    public async Task C_WithSubtitlesHidden_DoesNothing_AndShownAgain_ItWorks()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseSubtitles([English], Track);
        _buttons.SetShown(PlayerButton.Subtitles, false);

        viewModel.ToggleSubtitles();

        Assert.Empty(Playback.SubtitlePicks);
        Assert.True(viewModel.IsSubtitleOn);
        _buttons.SetShown(PlayerButton.Subtitles, true);
        viewModel.ToggleSubtitles();
        Assert.Equal([null], Playback.SubtitlePicks);
    }

    [Fact]
    public async Task TheChapterKeys_WithChaptersHidden_DoNothing()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 2700, null)]);
        At(30);
        _buttons.SetShown(PlayerButton.Chapters, false);

        viewModel.SeekToAdjacentChapter(forward: true);

        Assert.Empty(Playback.Seeks);
        Assert.True(viewModel.HasChapters);
    }

    [Fact]
    public async Task HidingPlaybackInfo_ClosesIt_AndItDoesNotOpenWhileHidden()
    {
        var viewModel = await PlayingAsync();
        viewModel.TogglePlaybackInfoCommand.Execute(null);
        Assert.True(viewModel.IsPlaybackInfoShown);

        _buttons.SetShown(PlayerButton.PlaybackInfo, false);
        Assert.False(viewModel.IsPlaybackInfoShown);
        viewModel.TogglePlaybackInfoCommand.Execute(null);

        Assert.False(viewModel.IsPlaybackInfoShown);
        _buttons.SetShown(PlayerButton.PlaybackInfo, true);
        viewModel.TogglePlaybackInfoCommand.Execute(null);
        Assert.True(viewModel.IsPlaybackInfoShown);
    }

    [Fact]
    public async Task HidingFillScreen_ShowsTheWholePictureAgain_AndFillDoesNotComeBackWhileHidden()
    {
        var viewModel = await PlayingAsync();
        viewModel.ToggleFillCommand.Execute(null);
        Assert.True(_engine.Fill);

        _buttons.SetShown(PlayerButton.Fill, false);
        Assert.False(viewModel.IsFillingScreen);
        Assert.False(_engine.Fill);
        viewModel.ToggleFillCommand.Execute(null);

        Assert.False(viewModel.IsFillingScreen);
        Assert.False(_engine.Fill);
    }

    [Fact]
    public async Task HidingAnotherButton_LeavesPlaybackInfoAndFillAlone()
    {
        var viewModel = await PlayingAsync();
        viewModel.TogglePlaybackInfoCommand.Execute(null);
        viewModel.ToggleFillCommand.Execute(null);

        _buttons.SetShown(PlayerButton.Settings, false);

        Assert.True(viewModel.IsPlaybackInfoShown);
        Assert.True(viewModel.IsFillingScreen);
    }

    [Fact]
    public async Task EditMarkersHidden_TheMarkerPanelDoesNotOpen_ButOneThatIsOpenStays()
    {
        var viewModel = await MarkerEditorAsync();
        _buttons.SetShown(PlayerButton.EditMarkers, false);

        viewModel.ToggleMarkerEditCommand.Execute(null);
        Assert.False(viewModel.MarkerEditor.IsEditing);

        _buttons.SetShown(PlayerButton.EditMarkers, true);
        viewModel.ToggleMarkerEditCommand.Execute(null);
        _buttons.SetShown(PlayerButton.EditMarkers, false);
        Assert.True(viewModel.MarkerEditor.IsEditing);
        viewModel.ToggleMarkerEditCommand.Execute(null);
        Assert.False(viewModel.MarkerEditor.IsEditing);
    }

    private const string Track = "file:1:subtitle:0";
    private static readonly SubtitleInventoryItem English = new() { TrackId = Track, Source = "embedded", Language = "eng" };

    private void At(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(0.25));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(2700), null, HasStarted: true));
    }

    // An admin on the primary profile, who may edit markers, with a server file playing.
    private async Task<PlayerViewModel> MarkerEditorAsync()
    {
        var silo = new ScriptedSilo();
        var session = new FakeSessionManager
        {
            State = SessionState.Ready,
            ActiveServer = ScriptedSilo.Server,
            ProfileId = "1",
            Account = new Account("1", "elena", "", "admin", [], true, false, null),
        };
        silo.On(HttpMethod.Get, "api/v2/profiles", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [ { "id": "1", "name": "Elena", "is_primary": true, "has_pin": false, "is_child": false } ], "avatar_upload_enabled": false }"""));
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { PlayingFileId = "42", DurationSeconds = 1500 };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, permissions: new AccountPermissions(session, new ProfilesApi(silo.Http), new LogSink().For<AccountPermissions>()),
            buttons: _buttons);
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1500), null, HasStarted: true));
        for (int i = 0; i < 200 && !viewModel.CanEditMarkers; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(viewModel.CanEditMarkers);
        return viewModel;
    }
}
