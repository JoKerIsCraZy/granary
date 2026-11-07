using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Settings;

namespace Granary.Tests.ViewModels;

/// <summary>The next episode in the player page: the card, the screen, the countdown, and a movie's end.</summary>
public sealed class PlayerViewModelNextUpTests
{
    private const double Length = 2100;
    private static readonly PlaybackMarker Credits = new(WatchMarkerKinds.Credits, 2040, 2100);
    private static readonly PlayingEpisode Ted = new("episode-1-3", "series-ted", "ted", 1, 3);
    private static readonly NextEpisode Next = new("episode-1-4", 1, 4, "Schrei der Verlorenen")
    {
        AirDate = "2024-01-11",
        RuntimeMinutes = 37,
        Overview = "Ted and John.",
    };

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeUpNextSource _source = new();
    private readonly AutoPlayStreak _streak = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly PlaybackStops _stops = new();
    private EpisodeSpoilerSettings? _spoilers;
    private double _position;

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync(PlayingEpisode? episode, MarkerSettings? settings = null)
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = Length,
                Episode = episode,
                Preferences = PlaybackPreferences.Default with { Markers = settings ?? MarkerSettings.Default },
            };
            _playbacks.Add(playback);
            return playback;
        }, _stops, _time, _source, _streak, spoilers: _spoilers);
        _navigation.NavigateTo(AppRoute.Item, "detail");
        _navigation.NavigateTo(AppRoute.Player);
        await viewModel.StartAsync(PlayRequest.Resume(episode?.ContentId ?? "movie-1"));
        Playback.RaiseMarkers([Credits]);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    private void PlayTo(double seconds)
    {
        _position = _position == 0 ? seconds - 10 : _position;
        for (; _position <= seconds; _position += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(_position), TimeSpan.FromSeconds(Length), null, HasStarted: true));
        }
    }

    [Fact]
    public async Task AnEpisode_HasPreviousAndNextButtons_ThatStartThemFromWhereTheProfileLeftThem()
    {
        _source.Previous = Next with { ContentId = "episode-1-2", EpisodeNumber = 2 };
        var viewModel = await PlayingAsync(Ted);

        Assert.True(viewModel.ShowsEpisodeSlots);
        Assert.True(viewModel.HasPreviousEpisode);
        Assert.True(viewModel.HasNextEpisode);

        await viewModel.PlayPreviousEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(PlayRequest.Resume("episode-1-2"), Playback.Started);
        Assert.Equal(1, _playbacks[0].Stops);
    }

    [Fact]
    public async Task APlayback_CountsAsPlaying_UntilItsStopEnded()
    {
        var viewModel = await PlayingAsync(Ted);
        Assert.True(_stops.IsPlaying);

        viewModel.Leave();

        Assert.True(await _stops.WhenStoppedAsync(TimeSpan.FromSeconds(5)));
        Assert.False(_stops.IsPlaying);
    }

    [Fact]
    public async Task TheSeasonsFirstEpisode_KeepsBothSlots_WithOnlyNext()
    {
        var viewModel = await PlayingAsync(Ted);

        Assert.True(viewModel.ShowsEpisodeSlots);
        Assert.False(viewModel.HasPreviousEpisode);

        await viewModel.PlayNextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(PlayRequest.Resume("episode-1-4"), Playback.Started);
    }

    [Fact]
    public async Task AMovie_HasNoEpisodeSlots()
    {
        var viewModel = await PlayingAsync(null);

        Assert.False(viewModel.ShowsEpisodeSlots);
    }

    [Fact]
    public async Task TheCredits_BringTheCard_AndPlayNowStartsTheNextEpisodeFromItsStart()
    {
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2030);
        Assert.False(viewModel.IsNextUpCardShown);

        PlayTo(2045);

        Assert.True(viewModel.IsNextUpCardShown);
        Assert.Equal("UP NEXT IN 55S", viewModel.NextUpCardHeading);
        Assert.Equal("S1:E4 — Schrei der Verlorenen", viewModel.NextUpEpisodeText);

        viewModel.PlayNextNowCommand.Execute(null);

        Assert.Equal(PlayRequest.FromStart("episode-1-4"), Playback.Started);
        Assert.Equal(1, _playbacks[0].Stops);
        Assert.False(viewModel.IsNextUpCardShown);
    }

    [Fact]
    public async Task ThirtySecondsBeforeTheEnd_TheScreenShows_WithTheEpisodeAndOnDeck()
    {
        _source.OnDeck = [new MediaCard("movie-9", CatalogItemTypes.Movie, "Alles Routine")];
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2071);

        Assert.True(viewModel.IsNextUpScreenShown);
        Assert.False(viewModel.IsNextUpCardShown);
        Assert.Equal("ted", viewModel.NextUpSeriesTitle);
        Assert.Equal("S1:E4", viewModel.NextUpCode);
        Assert.Equal("11 January 2024 • 37m", viewModel.NextUpMeta);
        Assert.Equal("series-ted", _source.OnDeckExcept);
        Assert.Equal("Alles Routine", Assert.Single(viewModel.OnDeck).Title);
        Assert.False(viewModel.IsNextUpCountingDown);
    }

    [Fact]
    public async Task AtTheEnd_TheCountdownPlaysTheNextEpisode_AndCountsTowardsTheGate()
    {
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2099);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        Assert.True(viewModel.IsNextUpCountingDown);
        Assert.Equal("10s", viewModel.NextUpSecondsText);
        var ended = Playback;

        _time.Advance(TimeSpan.FromSeconds(10.1));

        Assert.NotSame(ended, Playback);
        Assert.Equal(PlayRequest.FromStart("episode-1-4"), Playback.Started);
        Assert.Equal(1, _streak.Count);
    }

    [Fact]
    public async Task KeepWatching_ReturnsToTheVideo_AndTheEndThenWaits()
    {
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2071);

        viewModel.KeepWatchingCommand.Execute(null);
        Assert.False(viewModel.IsNextUpScreenShown);

        PlayTo(2099);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.True(viewModel.IsNextUpScreenShown);
        Assert.False(viewModel.IsNextUpCountingDown);
    }

    [Fact]
    public async Task TheAutoPlaySwitch_IsSaved_AndStopsTheCountdown()
    {
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2099);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        viewModel.ToggleAutoPlayCommand.Execute(null);

        Assert.Equal([false], Playback.AutoPlaySwitches);
        Assert.Equal("Auto-play is off", viewModel.AutoPlayText);
        Assert.False(viewModel.IsNextUpCountingDown);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task TheSkipPill_GivesWayToTheCard()
    {
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2045);

        Assert.True(viewModel.IsNextUpCardShown);
        Assert.False(viewModel.HasSkipPrompt);
        viewModel.CancelNextUpCommand.Execute(null);
        PlayTo(2046);
        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("Skip Credits", viewModel.SkipLabel);
    }

    [Fact]
    public async Task ATitleTheViewerStarts_OpensTheGateAgain_TheCountdownsStartDoesNot()
    {
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2099);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        _streak.RecordAutomatic();
        _time.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(2, _streak.Count);

        await viewModel.StartAsync(PlayRequest.Resume("episode-1-9"));

        Assert.Equal(0, _streak.Count);
    }

    [Fact]
    public async Task AtTheGate_TheScreenAsksWhetherYouAreStillWatching_AndWaits()
    {
        var viewModel = await PlayingAsync(Ted);
        // Two episodes before this one started by themselves, and this one's countdown makes three.
        _streak.RecordAutomatic();
        _streak.RecordAutomatic();
        PlayTo(2099);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        Assert.Equal("PLAYING NEXT", viewModel.NextUpScreenLabel);
        _time.Advance(TimeSpan.FromSeconds(10.1));
        var waiting = Playback;

        waiting.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _position = 0;
        PlayTo(2099);
        waiting.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        _time.Advance(TimeSpan.FromSeconds(10.1));

        Assert.Equal("STILL WATCHING?", viewModel.NextUpScreenLabel);
        Assert.False(viewModel.IsNextUpCountingDown);
        Assert.Same(waiting, Playback);
    }

    [Fact]
    public async Task AMoviesEnd_LeavesThePlayer()
    {
        var viewModel = await PlayingAsync(episode: null);
        PlayTo(2099);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.False(viewModel.IsNextUpScreenShown);
    }

    [Fact]
    public async Task TheScreen_BlursAnUnwatchedNextEpisodesStill_AndLeavesItsDescriptionOut()
    {
        _spoilers = await SpoilerSwitches.StartedAsync();
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2071);

        Assert.True(viewModel.IsNextUpScreenShown);
        Assert.True(viewModel.NextUpBlursStill);
        Assert.Equal("", viewModel.NextUpOverview);
        // The title stays.
        Assert.Equal("Schrei der Verlorenen", viewModel.NextUpTitle);
    }

    [Fact]
    public async Task TheScreen_ShowsANextEpisodeAlreadyStarted()
    {
        _spoilers = await SpoilerSwitches.StartedAsync();
        _source.NextOne = Next with { IsUnwatched = false };
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2071);

        Assert.False(viewModel.NextUpBlursStill);
        Assert.Equal("Ted and John.", viewModel.NextUpOverview);
    }

    [Fact]
    public async Task TheScreen_KeepsTheSeriesArtStandingInForAStill()
    {
        _spoilers = await SpoilerSwitches.StartedAsync();
        _source.NextOne = Next with { StillIsEpisodeStill = false };
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2071);

        Assert.False(viewModel.NextUpBlursStill);
        Assert.Equal("", viewModel.NextUpOverview);
    }

    [Fact]
    public async Task TheScreen_HidesNothing_WhileTheProfileHidesNothing()
    {
        _spoilers = await SpoilerSwitches.StartedAsync(images: false, overviews: false);
        var viewModel = await PlayingAsync(Ted);

        PlayTo(2071);

        Assert.False(viewModel.NextUpBlursStill);
        Assert.Equal("Ted and John.", viewModel.NextUpOverview);
    }

    [Fact]
    public async Task ASwitchChangedWhilePlaying_ShowsOnTheScreen()
    {
        _spoilers = await SpoilerSwitches.StartedAsync(images: false, overviews: false);
        var viewModel = await PlayingAsync(Ted);
        PlayTo(2071);

        await _spoilers.SetHidesImagesAsync(true);
        await _spoilers.SetHidesOverviewsAsync(true);
        PlayTo(2072);

        Assert.True(viewModel.NextUpBlursStill);
        Assert.Equal("", viewModel.NextUpOverview);
    }

    private sealed class FakeUpNextSource : IUpNextSource
    {
        public IReadOnlyList<MediaCard> OnDeck { get; set; } = [];

        public string? OnDeckExcept { get; private set; }

        public NextEpisode NextOne { get; set; } = Next;

        public Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) =>
            Task.FromResult<NextEpisode?>(current == Ted ? NextOne : null);

        public NextEpisode? Previous { get; set; }

        public Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) =>
            Task.FromResult(Previous);

        public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken)
        {
            OnDeckExcept = exceptSeriesId;
            return Task.FromResult(OnDeck);
        }
    }
}
