using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The player's volume and mute, kept for this PC as the web player keeps them in localStorage: every title starts with the
/// viewer's last, and only the viewer's own changes are kept, once they settle or as the player closes.
/// </summary>
public sealed class PlayerViewModelVolumeTests
{
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(500);

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryPlaybackDevicePreferences _preferences = new();

    private PlayerViewModel Create() => Create(_preferences);

    private PlayerViewModel Create(IPlaybackDevicePreferences? preferences) =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => new FakeServerPlayback(), new PlaybackStops(), _time,
            devicePreferences: preferences);

    private static Task PlayFileAsync(PlayerViewModel viewModel) => viewModel.StartAsync(PlayRequest.Direct(@"C:\media\tone.wav"));

    // Granary started again, or mpv did: the engine is back at mpv's own volume, not muted.
    private void EngineStartsAgain() => _engine.Current = PlaybackSnapshot.Idle;

    private static PlaybackSnapshot Playing() => new(true, false, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(30), null, HasStarted: true);

    [Fact]
    public async Task AVolumeSetInOneTitle_IsTheNextTitlesStart()
    {
        using (var first = Create())
        {
            await PlayFileAsync(first);
            first.Volume = 40;
        }
        EngineStartsAgain();

        using var next = Create();
        await PlayFileAsync(next);

        Assert.Equal(40, _engine.Current.Volume);
        Assert.False(_engine.Current.IsMuted);
        Assert.Equal(40, next.Volume);
        Assert.Equal([new PlayerVolume(40, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public void ANewPlayer_ShowsTheKeptVolumeAndMute_AtOnce()
    {
        _preferences.Volume = new PlayerVolume(40, IsMuted: true);

        using var viewModel = Create();

        Assert.Equal(40, viewModel.Volume);
        Assert.True(viewModel.IsMuted);
        Assert.Empty(_preferences.SavedVolume);
    }

    [Fact]
    public async Task WithoutAKeptVolume_ATitleKeepsTheEnginesOwn()
    {
        // As the viewer's own mpv.conf may set one.
        _engine.Current = PlaybackSnapshot.Idle with { Volume = 70 };
        using var viewModel = Create();

        await PlayFileAsync(viewModel);
        _time.Advance(Settled);

        Assert.Equal(70, _engine.Current.Volume);
        Assert.False(_engine.Current.IsMuted);
        Assert.Empty(_preferences.SavedVolume);
    }

    [Fact]
    public async Task AKeptMute_StartsTheTitleMuted_AndUnmuteGoesBackToTheKeptVolume()
    {
        _preferences.Volume = new PlayerVolume(40, IsMuted: true);
        using var viewModel = Create();

        await PlayFileAsync(viewModel);
        Assert.True(_engine.Current.IsMuted);
        Assert.Equal(40, _engine.Current.Volume);

        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.False(_engine.Current.IsMuted);
        Assert.Equal(40, _engine.Current.Volume);
        Assert.Equal([new PlayerVolume(40, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task Mute_IsKept_WithTheVolumeToComeBackTo()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        viewModel.Volume = 40;

        // M, or Mute in the minimized bar.
        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.Equal([true], _engine.MuteRequests);
        Assert.True(_engine.Current.IsMuted);
        Assert.Equal(40, _engine.Current.Volume);
        Assert.Equal([new PlayerVolume(40, IsMuted: true)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task AVolumeOfZero_IsNotAMute()
    {
        using (var viewModel = Create())
        {
            await PlayFileAsync(viewModel);
            viewModel.Volume = 0;
        }
        EngineStartsAgain();

        using var next = Create();
        await PlayFileAsync(next);

        Assert.Equal(0, _engine.Current.Volume);
        Assert.False(_engine.Current.IsMuted);
        Assert.False(next.IsMuted);
    }

    [Fact]
    public async Task AVolumeKeyWhileMuted_Unmutes_AndThatIsKeptToo()
    {
        _preferences.Volume = new PlayerVolume(40, IsMuted: true);
        using var viewModel = Create();
        await PlayFileAsync(viewModel);

        // Up as the volume key, in the full player or the minimized bar.
        viewModel.ChangeVolume(+5);
        _time.Advance(Settled);

        Assert.False(_engine.Current.IsMuted);
        Assert.Equal(45, _engine.Current.Volume);
        Assert.Equal([new PlayerVolume(45, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task ADragAlongTheSlider_IsSavedOnce_WhenItSettles()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);

        foreach (double volume in new[] { 90.0, 75, 60 })
        {
            viewModel.Volume = volume;
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        viewModel.Volume = 45;

        _time.Advance(Settled - TimeSpan.FromMilliseconds(1));
        Assert.Empty(_preferences.SavedVolume);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal([new PlayerVolume(45, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task ClosingThePlayerRightAfterAChange_StillKeepsIt_Once()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        viewModel.Volume = 40;

        viewModel.CloseCommand.Execute(null);
        Assert.Equal([new PlayerVolume(40, IsMuted: false)], _preferences.SavedVolume);
        _time.Advance(Settled);

        Assert.Equal([new PlayerVolume(40, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task AVolumeTheEngineReportsByItself_PlaysButIsNotKept_AndTheNextTitleStartsWithTheViewers()
    {
        // An administrator's set_volume, or a new mpv's own volume: not the viewer's change.
        _preferences.Volume = new PlayerVolume(40, IsMuted: false);
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing() with { Volume = 40 });

        _engine.RaiseState(Playing() with { Volume = 60 });
        _time.Advance(Settled);
        Assert.Equal(60, viewModel.Volume);
        Assert.Empty(_preferences.SavedVolume);

        await PlayFileAsync(viewModel);

        Assert.Equal(40, _engine.Current.Volume);
        Assert.Equal(40, viewModel.Volume);
        Assert.Empty(_preferences.SavedVolume);
    }

    [Fact]
    public async Task MuteAfterAVolumeFromElsewhere_KeepsTheViewersOwnVolume()
    {
        _preferences.Volume = new PlayerVolume(40, IsMuted: false);
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing() with { Volume = 40 });
        _engine.RaiseState(Playing() with { Volume = 60 });
        Assert.Equal(60, viewModel.Volume);

        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.Equal([new PlayerVolume(40, IsMuted: true)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task ADragAndBack_BeforeTheEngineAnswers_ReachesTheEngine_AndIsKept()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        // mpv says its new volume a moment after it was set: until then it still says 100.
        _engine.ReportsVolumeLater = true;

        viewModel.Volume = 60;
        viewModel.Volume = 100;
        _time.Advance(Settled);

        Assert.Equal([60.0, 100], _engine.VolumeRequests);
        Assert.Equal([new PlayerVolume(100, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task AVolumeChangeRightAfterMute_EndsTheMute_BeforeTheEngineAnswers()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        _engine.ReportsVolumeLater = true;

        viewModel.ToggleMuteCommand.Execute(null);
        viewModel.Volume = 50;
        _time.Advance(Settled);

        Assert.Equal([true, false], _engine.MuteRequests);
        Assert.Equal([50.0], _engine.VolumeRequests);
        Assert.Equal([new PlayerVolume(50, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task QuickMutePresses_BeforeTheEngineAnswers_TakeTurns()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        _engine.ReportsVolumeLater = true;

        viewModel.ToggleMuteCommand.Execute(null);
        viewModel.ToggleMuteCommand.Execute(null);
        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.Equal([true, false, true], _engine.MuteRequests);
        Assert.True(viewModel.IsMuted);
        Assert.Equal([new PlayerVolume(100, IsMuted: true)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task ASnapshotFromBeforeTheViewersChange_DoesNotMoveTheSliderOrTheMuteBack()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        _engine.ReportsVolumeLater = true;

        viewModel.Volume = 60;
        viewModel.ToggleMuteCommand.Execute(null);
        // A position mpv sent before it took the change.
        _engine.RaiseState(Playing());
        Assert.Equal((60.0, true), (viewModel.Volume, viewModel.IsMuted));

        // mpv took it; what it reports from then on shows, a volume set from elsewhere too.
        _engine.RaiseState(Playing() with { Volume = 60, IsMuted = true });
        _engine.RaiseState(Playing() with { Volume = 80, IsMuted = true });

        Assert.Equal((80.0, true), (viewModel.Volume, viewModel.IsMuted));
    }

    [Fact]
    public async Task AChangeTheEngineNeverReports_GivesWayToWhatItSays_AfterASecond()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        _engine.ReportsVolumeLater = true;

        viewModel.Volume = 60;
        _time.Advance(TimeSpan.FromMilliseconds(999));
        _engine.RaiseState(Playing());
        Assert.Equal(60, viewModel.Volume);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _engine.RaiseState(Playing());

        Assert.Equal(100, viewModel.Volume);
        Assert.Equal([60.0], _engine.VolumeRequests);
    }

    [Fact]
    public async Task AnUnmuteTheEngineNeverReports_ShowsItsMuteASecondLater_ThoughTheSliderKeepsMoving()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing() with { IsMuted = true });
        _engine.ReportsVolumeLater = true;

        viewModel.ToggleMuteCommand.Execute(null);
        for (int step = 1; step <= 3; step++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(300));
            viewModel.Volume = 100 - (10 * step);
            _engine.RaiseState(Playing() with { IsMuted = true });
            Assert.False(viewModel.IsMuted);
        }
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _engine.RaiseState(Playing() with { Volume = 70, IsMuted = true });

        Assert.True(viewModel.IsMuted);
        // The slider asked for no unmute of its own while the one of Mute was on its way.
        Assert.Equal([false], _engine.MuteRequests);
    }

    [Fact]
    public async Task AVolumeTheEngineNeverReports_ShowsItsLevelASecondLater_ThoughMuteIsPressed()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing());
        _engine.ReportsVolumeLater = true;

        viewModel.Volume = 60;
        _time.Advance(TimeSpan.FromMilliseconds(600));
        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        viewModel.ToggleMuteCommand.Execute(null);
        _engine.RaiseState(Playing());
        Assert.Equal(60, viewModel.Volume);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _engine.RaiseState(Playing());

        Assert.Equal(100, viewModel.Volume);
    }

    [Fact]
    public async Task AFirstMute_WithoutAKeptVolume_KeepsTheLevelThatPlays()
    {
        // The viewer's own mpv.conf set 70: muting keeps that level to come back to, as the viewer's own from then on.
        _engine.Current = PlaybackSnapshot.Idle with { Volume = 70 };
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        _engine.RaiseState(Playing() with { Volume = 70 });

        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.Equal([true], _engine.MuteRequests);
        Assert.True(_engine.Current.IsMuted);
        Assert.Equal([new PlayerVolume(70, IsMuted: true)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task AFirstMute_BeforeTheEngineSaysAnything_KeepsTheEnginesLevel()
    {
        // mpv runs at 70 from the viewer's mpv.conf; this player has not been told so yet, and still shows its 100.
        _engine.Current = PlaybackSnapshot.Idle with { Volume = 70 };
        using var viewModel = Create();
        await PlayFileAsync(viewModel);

        viewModel.ToggleMuteCommand.Execute(null);
        _time.Advance(Settled);

        Assert.Equal([true], _engine.MuteRequests);
        Assert.Equal([new PlayerVolume(70, IsMuted: true)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task AServerTitle_AndTheNextOne_StartWithTheViewersVolume()
    {
        _preferences.Volume = new PlayerVolume(40, IsMuted: false);
        using var viewModel = Create();

        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Assert.Equal(40, _engine.Current.Volume);
        viewModel.Volume = 25;
        EngineStartsAgain();
        await viewModel.StartAsync(PlayRequest.Resume("episode-2"));

        Assert.Equal(25, _engine.Current.Volume);
    }

    [Fact]
    public async Task WithoutThisPcsChoices_TheVolumeStillCarriesToTheNextTitle()
    {
        using var viewModel = Create(null);
        await PlayFileAsync(viewModel);
        viewModel.Volume = 40;
        viewModel.ToggleMuteCommand.Execute(null);
        EngineStartsAgain();

        await PlayFileAsync(viewModel);

        Assert.Equal(40, _engine.Current.Volume);
        Assert.True(_engine.Current.IsMuted);
        Assert.Null(_time.NextDue);
    }
}
