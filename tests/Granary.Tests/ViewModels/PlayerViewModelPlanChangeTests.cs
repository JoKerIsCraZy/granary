using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// While a quality, version or track switch is planned anew: the settings button says "…" as the web's QualityMenu does,
/// the centre spinner shows after the web's anti-flash delay, and the corner mark names the change.
/// </summary>
public sealed class PlayerViewModelPlanChangeTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(90), null, HasStarted: true));
        return viewModel;
    }

    private static readonly AvailableQuality[] Rungs =
    [
        new() { Label = "1080p-high", DisplayName = "1080p High" },
        new() { Label = "720p-medium", DisplayName = "720p Medium" },
    ];

    [Fact]
    public async Task AChange_ShowsAtOnce_AndEndsWithThePlaybacks()
    {
        var viewModel = await PlayingAsync();
        Assert.False(viewModel.IsChangingPlan);

        Playback.RaisePlanChange(PlanChange.Quality);
        Assert.True(viewModel.IsChangingPlan);
        Assert.Equal("Switching quality", viewModel.PlanChangeText);

        Playback.RaisePlanChange(PlanChange.None);
        Assert.False(viewModel.IsChangingPlan);
        Assert.Equal("", viewModel.PlanChangeText);
    }

    [Theory]
    [InlineData(PlanChange.Quality, "Switching quality")]
    [InlineData(PlanChange.Version, "Switching version")]
    [InlineData(PlanChange.Audio, "Switching audio")]
    [InlineData(PlanChange.Subtitles, "Switching subtitles")]
    [InlineData(PlanChange.Seek, "Seeking")]
    [InlineData(PlanChange.Restart, "Restarting playback")]
    public async Task EachChange_SaysWhatItDoes(PlanChange change, string text)
    {
        var viewModel = await PlayingAsync();

        Playback.RaisePlanChange(change);

        Assert.Equal(text, viewModel.PlanChangeText);
    }

    [Fact]
    public async Task TheSpinner_ShowsOnceAChangeHasTakenHalfASecond()
    {
        var viewModel = await PlayingAsync();

        Playback.RaisePlanChange(PlanChange.Version);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(viewModel.IsWaiting);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(viewModel.IsWaiting);

        Playback.RaisePlanChange(PlanChange.None);
        Assert.False(viewModel.IsWaiting);
    }

    [Fact]
    public async Task ABriefChange_DoesNotFlashTheSpinner()
    {
        var viewModel = await PlayingAsync();
        var waits = new List<bool>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsWaiting))
            {
                waits.Add(viewModel.IsWaiting);
            }
        };

        Playback.RaisePlanChange(PlanChange.Audio);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        Playback.RaisePlanChange(PlanChange.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(waits);
    }

    [Fact]
    public async Task BufferingToo_ShowsTheSpinnerOnlyAfterHalfASecond()
    {
        var viewModel = await PlayingAsync();

        _engine.RaiseState(_engine.Current with { IsBuffering = true });
        Assert.True(viewModel.IsBuffering);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(viewModel.IsWaiting);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(viewModel.IsWaiting);

        _engine.RaiseState(_engine.Current with { IsBuffering = false });
        Assert.False(viewModel.IsWaiting);
    }

    [Fact]
    public async Task AChangeThatFollowsAStall_KeepsTheSpinner()
    {
        var viewModel = await PlayingAsync();
        _engine.RaiseState(_engine.Current with { IsBuffering = true });
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Playback.RaisePlanChange(PlanChange.Seek);
        _engine.RaiseState(_engine.Current with { IsBuffering = false });

        Assert.True(viewModel.IsWaiting);
    }

    [Fact]
    public async Task TheSettingsButton_SaysEllipsis_WhileTheQualityChanges()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlan(Rungs, "auto", "720p-medium");
        Assert.Equal("Auto", viewModel.SettingsLabel);

        Playback.RaisePlanChange(PlanChange.Quality);
        Assert.Equal("…", viewModel.SettingsLabel);

        Playback.RaisePlan(Rungs, "1080p-high", "1080p-high");
        Assert.Equal("…", viewModel.SettingsLabel);
        Playback.RaisePlanChange(PlanChange.None);
        Assert.Equal("1080p High", viewModel.SettingsLabel);
    }

    [Fact]
    public async Task WithoutAQualityMenu_TheButtonNamesNoQuality_EvenDuringAChange()
    {
        var viewModel = await PlayingAsync();

        Playback.RaisePlanChange(PlanChange.Version);

        Assert.Equal("", viewModel.SettingsLabel);
    }

    [Fact]
    public async Task Leaving_EndsTheChange_AndItsSpinner()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlanChange(PlanChange.Quality);
        _time.Advance(TimeSpan.FromSeconds(1));

        viewModel.Leave();

        Assert.False(viewModel.IsChangingPlan);
        Assert.False(viewModel.IsWaiting);
    }

    [Fact]
    public async Task AChangeOfATitleLeftBehind_IsNotShown()
    {
        var viewModel = await PlayingAsync();
        var left = Playback;
        await viewModel.StartAsync(PlayRequest.Resume("movie-2"));

        left.RaisePlanChange(PlanChange.Quality);

        Assert.False(viewModel.IsChangingPlan);
    }
}
