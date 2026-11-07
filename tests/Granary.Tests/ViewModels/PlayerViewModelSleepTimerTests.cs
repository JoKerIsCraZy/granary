using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The sleep timer: the button's countdown, its menu, and what happens when it runs out or the episode ends.</summary>
public sealed class PlayerViewModelSleepTimerTests
{
    private const double Length = 2100;
    private static readonly PlaybackMarker Credits = new(WatchMarkerKinds.Credits, 2040, 2100);
    private static readonly PlayingEpisode Ted = new("episode-1-3", "series-ted", "ted", 1, 3);
    private static readonly PlayingEpisode Fourth = new("episode-1-4", "series-ted", "ted", 1, 4);
    private static readonly NextEpisode Next = new("episode-1-4", 1, 4, "Schrei der Verlorenen");

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeUpNextSource _source = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private double _position;

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync(PlayingEpisode? episode = null, int sleepTimerMinutes = PlaybackPreferences.DefaultSleepTimerMinutes)
    {
        var episodes = new Queue<PlayingEpisode?>([episode, Fourth]);
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = Length,
                Episode = episodes.Count > 0 ? episodes.Dequeue() : null,
                Preferences = PlaybackPreferences.Default with { SleepTimerMinutes = sleepTimerMinutes },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, _source);
        _navigation.NavigateTo(AppRoute.Item, "detail");
        _navigation.NavigateTo(AppRoute.Player);
        await viewModel.StartAsync(PlayRequest.Resume(episode?.ContentId ?? "movie-1"));
        Plays();
        return viewModel;
    }

    // The playback that started last plays from its start, with its credits marked.
    private void Plays()
    {
        Playback.RaiseMarkers([Credits]);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _position = 0;
    }

    private void PlayTo(double seconds)
    {
        _position = _position == 0 ? seconds - 10 : _position;
        for (; _position <= seconds; _position += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            _engine.RaiseState(new PlaybackSnapshot(true, _engine.Current.IsPaused, TimeSpan.FromSeconds(_position), TimeSpan.FromSeconds(Length), null,
                HasStarted: true));
        }
    }

    private void PlaysToItsEnd()
    {
        PlayTo(2099);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
    }

    private static void Choose(PlayerViewModel viewModel, string label) =>
        viewModel.ChooseSleepTimer(viewModel.SleepTimerOptions.Single(o => o.Label == label));

    [Fact]
    public async Task TheMenu_HasTheWebsDurations_AndTheEndOfTheEpisode()
    {
        var viewModel = await PlayingAsync(Ted);

        Assert.Equal(["5 min", "15 min", "30 min", "45 min", "60 min", "End of episode"], viewModel.SleepTimerOptions.Select(o => o.Label));
        Assert.False(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep", viewModel.SleepTimerText);
    }

    [Fact]
    public async Task AMovie_EndsTheMenuWithTheEndOfTheMovie()
    {
        var viewModel = await PlayingAsync();

        Assert.Equal("End of movie", viewModel.SleepTimerOptions[^1].Label);
    }

    [Fact]
    public async Task ARunningTimer_CountsDownOnTheButton_AndTheMenuOffersTurnOff()
    {
        var viewModel = await PlayingAsync(Ted);

        Choose(viewModel, "30 min");

        Assert.True(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep 30:00", viewModel.SleepTimerText);
        Assert.Equal("30 min", viewModel.SleepTimerStatus);
        Assert.Equal("Turn off", viewModel.SleepTimerOptions[0].Label);
        var options = viewModel.SleepTimerOptions;
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Sleep 29:59", viewModel.SleepTimerText);
        // Screen readers and the open menu are left alone by the seconds.
        Assert.Equal("30 min", viewModel.SleepTimerStatus);
        Assert.Same(options, viewModel.SleepTimerOptions);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal("Sleep 29:00", viewModel.SleepTimerText);
        _time.Advance(TimeSpan.FromMinutes(28.5));
        Assert.Equal("Sleep 0:30", viewModel.SleepTimerText);
    }

    [Fact]
    public async Task WhenItRunsOut_PlaybackPauses_AndTheTimerIsOff()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");

        _time.Advance(TimeSpan.FromSeconds(299));
        Assert.False(_engine.Current.IsPaused);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(_engine.Current.IsPaused);
        Assert.False(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep", viewModel.SleepTimerText);
        Assert.Equal("", viewModel.SleepTimerStatus);
        Assert.DoesNotContain(viewModel.SleepTimerOptions, o => o.Action == SleepTimerAction.TurnOff);
    }

    [Fact]
    public async Task TurningItOff_StopsTheCountdown_AndNothingPauses()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");
        _time.Advance(TimeSpan.FromMinutes(2));

        Choose(viewModel, "Turn off");
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(_engine.Current.IsPaused);
        Assert.False(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep", viewModel.SleepTimerText);
    }

    [Fact]
    public async Task AnotherDuration_StartsTheCountdownAgain()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");
        _time.Advance(TimeSpan.FromMinutes(4));

        Choose(viewModel, "15 min");
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(_engine.Current.IsPaused);
        Assert.Equal("Sleep 13:00", viewModel.SleepTimerText);
    }

    [Fact]
    public async Task EndOfEpisode_StopsAtTheEnd_InsteadOfPlayingTheNext()
    {
        var viewModel = await PlayingAsync(Ted);

        Choose(viewModel, "End of episode");
        Assert.True(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep", viewModel.SleepTimerText);
        Assert.Equal("End of episode", viewModel.SleepTimerStatus);

        // The card names the next episode, but counts nothing down: it will not start by itself.
        PlayTo(2045);
        Assert.True(viewModel.IsNextUpCardShown);
        Assert.Equal("UP NEXT", viewModel.NextUpCardHeading);

        PlaysToItsEnd();
        Assert.True(viewModel.IsNextUpScreenShown);
        Assert.False(viewModel.IsNextUpCountingDown);
        Assert.False(viewModel.IsSleepTimerOn);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(_playbacks);
        Assert.Equal(0, _navigation.PlayerLeaves);
    }

    [Fact]
    public async Task EndOfEpisode_TurnedOffBeforeTheEnd_LetsTheNextEpisodeCome()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "End of episode");
        _time.Advance(TimeSpan.FromMinutes(1));

        Choose(viewModel, "Turn off");
        PlaysToItsEnd();

        Assert.True(viewModel.IsNextUpCountingDown);
    }

    [Fact]
    public async Task AfterTheEpisodeItStoppedAt_TheNextPlaysOnAsUsual()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "End of episode");
        PlaysToItsEnd();

        viewModel.PlayNextNowCommand.Execute(null);
        _source.HasNext = true;
        Plays();
        PlaysToItsEnd();

        Assert.True(viewModel.IsNextUpCountingDown);
    }

    [Fact]
    public async Task EndOfEpisode_GoesWithTheViewerToAnotherEpisode()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "End of episode");

        await viewModel.PlayNextEpisodeCommand.ExecuteAsync(null);
        _source.HasNext = true;
        Plays();
        PlaysToItsEnd();

        Assert.True(viewModel.IsNextUpScreenShown);
        Assert.False(viewModel.IsNextUpCountingDown);
    }

    [Fact]
    public async Task ADuration_RunsOnIntoTheNextEpisode_AndPausesIt()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "15 min");

        PlaysToItsEnd();
        _time.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(2, _playbacks.Count);
        Plays();

        Assert.True(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep 14:40", viewModel.SleepTimerText);
        _time.Advance(TimeSpan.FromSeconds(879));
        Assert.False(_engine.Current.IsPaused);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_engine.Current.IsPaused);
        Assert.False(viewModel.IsSleepTimerOn);
    }

    [Fact]
    public async Task ATimerThatRunsOutAtTheEnd_StopsTheCountdownToTheNextEpisode()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");
        _time.Advance(TimeSpan.FromSeconds(289));

        PlaysToItsEnd();
        Assert.True(viewModel.IsNextUpCountingDown);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(viewModel.IsSleepTimerOn);
        Assert.False(viewModel.IsNextUpCountingDown);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task ATimerThatRunsOutWhileTheNextEpisodeLoads_PausesItOnceItPlays()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");
        _time.Advance(TimeSpan.FromSeconds(279));
        PlaysToItsEnd();
        _time.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(PlayerPhase.Preparing, viewModel.Phase);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_engine.Current.IsPaused);
        Plays();

        Assert.True(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task LeavingThePlayer_EndsTheTimer()
    {
        var viewModel = await PlayingAsync(Ted);
        Choose(viewModel, "5 min");

        viewModel.Leave();

        Assert.False(viewModel.IsSleepTimerOn);
        Assert.Equal("Sleep", viewModel.SleepTimerText);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task TheMenu_OpensOnTheDurationThisDeviceStartsOn()
    {
        var viewModel = await PlayingAsync(Ted, sleepTimerMinutes: 45);

        Assert.Equal("45 min", Assert.Single(viewModel.SleepTimerOptions, o => o.IsDefault).Label);
    }

    [Fact]
    public async Task ADefaultThatIsNoPreset_GetsItsOwnRow_InOrder()
    {
        var viewModel = await PlayingAsync(Ted, sleepTimerMinutes: 90);

        Assert.Equal(["5 min", "15 min", "30 min", "45 min", "60 min", "90 min", "End of episode"], viewModel.SleepTimerOptions.Select(o => o.Label));
        Assert.Equal("90 min", Assert.Single(viewModel.SleepTimerOptions, o => o.IsDefault).Label);
    }

    [Fact]
    public async Task ADefaultOfZero_LeavesTheTimerWithoutOne()
    {
        var viewModel = await PlayingAsync(Ted, sleepTimerMinutes: 0);

        Assert.DoesNotContain(viewModel.SleepTimerOptions, o => o.IsDefault);
        Assert.Equal(6, viewModel.SleepTimerOptions.Count);
    }

    [Fact]
    public async Task ADurationPicked_IsSavedAsThisDevicesDefault_AndTheMenuOpensOnIt()
    {
        var viewModel = await PlayingAsync(Ted);

        Choose(viewModel, "15 min");
        Choose(viewModel, "End of episode");

        Assert.Equal([15], Playback.SleepTimerDefaults);
        Assert.Equal("15 min", Assert.Single(viewModel.SleepTimerOptions, o => o.IsDefault).Label);
    }

    private sealed class FakeUpNextSource : IUpNextSource
    {
        // The episode after the third is the fourth; after that, while set, the fourth again.
        public bool HasNext { get; set; }

        public Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) =>
            Task.FromResult<NextEpisode?>(current == Ted || HasNext ? Next : null);

        public Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) =>
            Task.FromResult<NextEpisode?>(null);

        public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaCard>>([]);
    }
}
