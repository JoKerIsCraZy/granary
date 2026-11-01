using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// While a quality, version or track switch is planned anew: the settings button says "…" as the web's QualityMenu does,
/// the centre spinner shows once the picture stops for the new stream (after the web's anti-flash delay), and the corner
/// mark names the change.
/// </summary>
public sealed class PlayerViewModelPlanChangeTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel Create(Granary.Core.Abstractions.IUiDispatcher? dispatcher = null) =>
        new(_engine, dispatcher ?? new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = Create();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(90), null, HasStarted: true));
        return viewModel;
    }

    // The playback hands the engine the new stream: the picture stops until its first frame.
    private void NewStreamLoads() => _engine.RaiseState(_engine.Current with { IsLoaded = false, HasStarted = false });

    private void NewStreamPlays() => _engine.RaiseState(_engine.Current with { IsLoaded = true, HasStarted = true });

    private static List<string> Changes(PlayerViewModel viewModel, string property)
    {
        var values = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property)
            {
                values.Add(typeof(PlayerViewModel).GetProperty(property)!.GetValue(viewModel)!.ToString()!);
            }
        };
        return values;
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
    public async Task WhileTheOldStreamPlaysOn_TheSpinnerStaysAway()
    {
        var viewModel = await PlayingAsync();

        Playback.RaisePlanChange(PlanChange.Version);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(viewModel.IsChangingPlan);
        Assert.False(viewModel.IsWaiting);
    }

    [Fact]
    public async Task TheSpinner_ShowsHalfASecondAfterThePictureStopsForTheNewStream()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlanChange(PlanChange.Version);
        _time.Advance(TimeSpan.FromSeconds(2));

        NewStreamLoads();
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(viewModel.IsWaiting);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(viewModel.IsWaiting);

        NewStreamPlays();
        Assert.False(viewModel.IsWaiting);
    }

    [Fact]
    public async Task ANewStreamThatComesQuickly_DoesNotFlashTheSpinner()
    {
        var viewModel = await PlayingAsync();
        var waits = Changes(viewModel, nameof(PlayerViewModel.IsWaiting));

        Playback.RaisePlanChange(PlanChange.Audio);
        NewStreamLoads();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        NewStreamPlays();
        Playback.RaisePlanChange(PlanChange.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(waits);
    }

    [Fact]
    public async Task AnOldStreamThatStallsDuringAChange_ShowsTheSpinner()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlanChange(PlanChange.Quality);

        _engine.RaiseState(_engine.Current with { IsBuffering = true });
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.True(viewModel.IsWaiting);
    }

    [Fact]
    public async Task Buffering_ShowsTheSpinnerOnlyAfterHalfASecond()
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
    public async Task AStallThatTurnsIntoANewStream_KeepsTheSpinner()
    {
        var viewModel = await PlayingAsync();
        _engine.RaiseState(_engine.Current with { IsBuffering = true });
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Playback.RaisePlanChange(PlanChange.Seek);
        _engine.RaiseState(_engine.Current with { IsBuffering = false, IsLoaded = false, HasStarted = false });

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
    public async Task AChange_IsAnnouncedOnce_WhenItHasLastedHalfASecond()
    {
        var viewModel = await PlayingAsync();
        var announced = Changes(viewModel, nameof(PlayerViewModel.PlanChangeAnnouncement));

        Playback.RaisePlanChange(PlanChange.Quality);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal("", viewModel.PlanChangeAnnouncement);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("Switching quality", viewModel.PlanChangeAnnouncement);

        // A seek the quality's stream led to goes on with the same change: nothing new to say.
        Playback.RaisePlanChange(PlanChange.Seek);
        _time.Advance(TimeSpan.FromSeconds(2));
        Playback.RaisePlanChange(PlanChange.None);

        Assert.Equal(["Switching quality", ""], announced);
    }

    [Fact]
    public async Task AChangeThatEndsWithinHalfASecond_IsNotAnnounced()
    {
        var viewModel = await PlayingAsync();
        var announced = Changes(viewModel, nameof(PlayerViewModel.PlanChangeAnnouncement));

        Playback.RaisePlanChange(PlanChange.Subtitles);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Playback.RaisePlanChange(PlanChange.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(announced);
    }

    [Fact]
    public async Task ALaterChange_IsAnnouncedAgain()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlanChange(PlanChange.Quality);
        _time.Advance(TimeSpan.FromSeconds(1));
        Playback.RaisePlanChange(PlanChange.None);

        Playback.RaisePlanChange(PlanChange.Version);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Switching version", viewModel.PlanChangeAnnouncement);
    }

    [Fact]
    public async Task Leaving_EndsTheChange_AndItsSpinner()
    {
        var viewModel = await PlayingAsync();
        Playback.RaisePlanChange(PlanChange.Quality);
        NewStreamLoads();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsWaiting);

        viewModel.Leave();

        Assert.False(viewModel.IsChangingPlan);
        Assert.False(viewModel.IsWaiting);
        Assert.Equal("", viewModel.PlanChangeAnnouncement);
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

    [Fact]
    public void AStatePostedBeforeDispose_StartsNoTimerAfterIt()
    {
        var dispatcher = new QueuedDispatcher();
        var viewModel = Create(dispatcher);
        Assert.Null(_time.NextDue);

        _engine.RaiseState(PlaybackSnapshot.Idle with { IsBuffering = true });
        viewModel.Dispose();
        dispatcher.RunAll();

        Assert.Null(_time.NextDue);
    }
}
