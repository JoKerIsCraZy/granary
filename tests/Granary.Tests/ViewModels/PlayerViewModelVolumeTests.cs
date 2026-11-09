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

        foreach (double volume in new[] { 90.0, 75, 60, 45 })
        {
            viewModel.Volume = volume;
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Empty(_preferences.SavedVolume);

        _time.Advance(Settled);

        Assert.Equal([new PlayerVolume(45, IsMuted: false)], _preferences.SavedVolume);
    }

    [Fact]
    public async Task ClosingThePlayerRightAfterAChange_StillKeepsIt_Once()
    {
        using var viewModel = Create();
        await PlayFileAsync(viewModel);
        viewModel.Volume = 40;

        viewModel.CloseCommand.Execute(null);
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
        _engine.RaiseState(Playing() with { Volume = 60 });

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
        Assert.Equal([new PlayerVolume(50, IsMuted: false)], _preferences.SavedVolume);
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
