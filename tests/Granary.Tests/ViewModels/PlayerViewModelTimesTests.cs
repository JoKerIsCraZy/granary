using System.Globalization;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The times under the player's title: the duration or the time left, as this PC chose last, and the clock time the
/// title ends. Some tests change the current culture, so the class runs on its own.
/// </summary>
[Collection(nameof(CurrentCultureChanges))]
public sealed class PlayerViewModelTimesTests
{
    // 45 minutes; the clock starts at 12:00 UTC, 14:00 on this PC.
    private const double Length = 2700;
    private static readonly TimeZoneInfo TwoHoursAhead =
        TimeZoneInfo.CreateCustomTimeZone("Granary test", TimeSpan.FromHours(2), "Granary test", "Granary test");

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new() { Zone = TwoHoursAhead };
    private readonly InMemoryPlaybackDevicePreferences _preferences = new();
    private FakeServerPlayback? _playback;

    private PlayerViewModel Create(IPlaybackDevicePreferences? preferences) =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => new FakeServerPlayback(), new PlaybackStops(), _time,
            devicePreferences: preferences);

    // A server title that plays from position seconds.
    private async Task<PlayerViewModel> PlayingAsync(double position, double? length = Length, bool paused = false)
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(),
            () => _playback = new FakeServerPlayback { DurationSeconds = length }, new PlaybackStops(), _time);
        await StartsAsync(viewModel, "movie-1");
        At(position, paused);
        return viewModel;
    }

    // A server title starts: its playback says it plays, and loads its stream into the engine, as ServerPlayback does.
    private async Task StartsAsync(PlayerViewModel viewModel, string contentId)
    {
        await viewModel.StartAsync(PlayRequest.Resume(contentId));
        _playback!.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Loads();
    }

    private void Loads() => _engine.Load(new MediaSource(new Uri("https://media.example/stream"), []));

    private void At(double position, bool paused = false, double? length = null, bool buffering = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(position),
            length is { } seconds ? TimeSpan.FromSeconds(seconds) : null, null, HasStarted: true, IsBuffering: buffering));

    private static string Time(int hour, int minute) => new DateTime(2026, 10, 1, hour, minute, 0).ToString("t", CultureInfo.CurrentCulture);

    private static string Ends(int hour, int minute) => "Ends " + Time(hour, minute);

    [Fact]
    public void AtFirst_TheTimeShowsTheDuration()
    {
        using var viewModel = Create(_preferences);

        Assert.False(viewModel.ShowsTimeLeft);
    }

    [Fact]
    public void ANewPlayer_ShowsTheTimeLeft_WhenThisPcChoseItLast()
    {
        _preferences.TimeLeft = true;

        using var viewModel = Create(_preferences);

        Assert.True(viewModel.ShowsTimeLeft);
        Assert.Empty(_preferences.SavedTimeLeft);
    }

    [Fact]
    public void AClick_SwitchesBetweenTheDurationAndTheTimeLeft_AndThisPcKeepsEachChoice()
    {
        using var viewModel = Create(_preferences);

        viewModel.ToggleTimeLeftCommand.Execute(null);
        Assert.True(viewModel.ShowsTimeLeft);
        viewModel.ToggleTimeLeftCommand.Execute(null);
        Assert.False(viewModel.ShowsTimeLeft);

        Assert.Equal([true, false], _preferences.SavedTimeLeft);
    }

    [Fact]
    public void WithoutThisPcsChoices_AClickStillSwitches()
    {
        using var viewModel = Create(null);

        viewModel.ToggleTimeLeftCommand.Execute(null);

        Assert.True(viewModel.ShowsTimeLeft);
    }

    [Fact]
    public async Task WhilePlaying_TheLineSaysWhenTheTitleEnds_OnThisPcsClock()
    {
        // 35 minutes left at 14:00.
        using var viewModel = await PlayingAsync(600);

        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
        Assert.Equal("Ends at " + Time(14, 35), viewModel.EndsAtSpoken);
    }

    [Theory]
    [InlineData(580, 35)]
    [InlineData(560, 36)]
    public async Task TheEnd_IsRoundedToTheMinute(double position, int minute)
    {
        // 35:20 and 35:40 left.
        using var viewModel = await PlayingAsync(position);

        Assert.Equal(Ends(14, minute), viewModel.EndsAtText);
    }

    [Theory]
    [InlineData("de-DE", "^Ends 14:35$")]
    [InlineData("en-US", @"^Ends 2:35\sPM$")]
    public async Task TheTime_IsThisPcsShortTime_In24Or12Hours(string culture, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            using var viewModel = await PlayingAsync(600);

            Assert.Matches(expected, viewModel.EndsAtText);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task WhilePlaying_TheEndStays()
    {
        using var viewModel = await PlayingAsync(600);

        for (int second = 1; second <= 90; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            At(600 + second);
        }

        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
    }

    [Fact]
    public async Task WhilePaused_TheEndMovesLater_AsTimePasses()
    {
        using var viewModel = await PlayingAsync(600, paused: true);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Ends(14, 36), viewModel.EndsAtText);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(Ends(14, 46), viewModel.EndsAtText);
    }

    [Fact]
    public async Task ASeek_MovesTheEnd()
    {
        using var viewModel = await PlayingAsync(600);

        viewModel.SeekTo(1800);
        At(1800);

        Assert.Equal(Ends(14, 15), viewModel.EndsAtText);
    }

    [Fact]
    public async Task AnotherSpeed_MovesTheEnd()
    {
        using var viewModel = await PlayingAsync(600);

        // 35 minutes of the title take 23:20 at 1.5×.
        viewModel.SetSpeed(1.5);

        Assert.Equal(Ends(14, 23), viewModel.EndsAtText);
    }

    [Fact]
    public async Task AtTheTurnOfAMinute_TheEndDoesNotFlickerWithThePositions()
    {
        // The end falls a moment either side of 14:35:30, as positions arrive a little early or late.
        using var viewModel = await PlayingAsync(570.1);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        for (int update = 0; update < 10; update++)
        {
            At(update % 2 == 0 ? 569.9 : 570.1);
        }

        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
        Assert.DoesNotContain(nameof(PlayerViewModel.EndsAtText), changes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(1e300)]
    [InlineData(200_000.0)]
    public async Task WithoutAKnownLength_OrAnEndADayAway_NoEndShows(double? length)
    {
        using var viewModel = await PlayingAsync(600, length);

        Assert.Equal("", viewModel.EndsAtText);
        Assert.Equal("", viewModel.EndsAtSpoken);
    }

    [Fact]
    public async Task AFilePlayedDirectly_EndsByItsOwnLength()
    {
        using var viewModel = Create(null);

        await viewModel.StartAsync(PlayRequest.Direct(@"C:\media\tone.wav"));
        At(60, length: 660);

        Assert.Equal(Ends(14, 10), viewModel.EndsAtText);
    }

    [Fact]
    public async Task AStreamWithoutAnEnd_ShowsNone()
    {
        using var viewModel = Create(null);

        await viewModel.StartAsync(PlayRequest.Direct("https://media.example/live.m3u8"));
        At(60);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("", viewModel.EndsAtText);
    }

    [Fact]
    public async Task OnceThePlayerIsLeft_NoEndShows_AndNothingTicks()
    {
        using var viewModel = await PlayingAsync(600, paused: true);

        viewModel.Leave();

        Assert.Equal("", viewModel.EndsAtText);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public async Task AtTheEnd_NoEndShows()
    {
        using var viewModel = await PlayingAsync(600);

        _playback!.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.Equal("", viewModel.EndsAtText);
    }

    [Fact]
    public async Task WhilePlaying_TheEndWaitsForThePositions_AndNothingTicks()
    {
        // 35:32 left: the end, 14:35:28, is two seconds short of the next minute.
        using var viewModel = await PlayingAsync(572);

        // The engine says nothing for a moment: the position it said last is no reason for the end to move.
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public async Task APause_StartsTheClock_AndPlayingAgainStopsIt()
    {
        using var viewModel = await PlayingAsync(600);

        At(600, paused: true);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Ends(14, 36), viewModel.EndsAtText);

        At(600);
        Assert.Null(_time.NextDue);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Ends(14, 36), viewModel.EndsAtText);
    }

    [Fact]
    public async Task ASeekWhilePaused_MovesTheEnd_AndTheClockRunsOn()
    {
        using var viewModel = await PlayingAsync(600, paused: true);

        viewModel.SeekTo(1800);
        At(1800, paused: true);
        Assert.Equal(Ends(14, 15), viewModel.EndsAtText);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Ends(14, 16), viewModel.EndsAtText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhileTheStreamWaits_TheEndMovesLater(bool reconnecting)
    {
        using var viewModel = await PlayingAsync(600);

        if (reconnecting)
        {
            _playback!.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        }
        else
        {
            At(600, buffering: true);
        }
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(Ends(14, 36), viewModel.EndsAtText);
    }

    [Fact]
    public async Task TheNextTitle_ShowsNoEnd_UntilItsOwnPositionComes()
    {
        // Ten seconds before the end of the first title.
        using var viewModel = await PlayingAsync(2690);
        Assert.Equal(Ends(14, 0), viewModel.EndsAtText);

        await viewModel.StartAsync(PlayRequest.Resume("movie-2"));
        _playback!.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        // The title before's last position, late: the new title's length less that would be a wrong end.
        At(2695);
        Assert.Equal("", viewModel.EndsAtText);

        Loads();
        At(0);
        Assert.Equal(Ends(14, 45), viewModel.EndsAtText);
    }

    [Fact]
    public async Task Disposed_NothingTicks_AndATickPostedBeforeDoesNothing()
    {
        var dispatcher = new QueuedDispatcher();
        var viewModel = new PlayerViewModel(_engine, dispatcher, new RecordingNavigation(),
            () => _playback = new FakeServerPlayback { DurationSeconds = Length }, new PlaybackStops(), _time);
        await StartsAsync(viewModel, "movie-1");
        At(600, paused: true);
        dispatcher.RunAll();
        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
        _time.Advance(TimeSpan.FromSeconds(1));

        viewModel.Dispose();
        Assert.Null(_time.NextDue);
        dispatcher.RunAll();

        Assert.Null(_time.NextDue);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public async Task ASpeedThatIsNoSpeed_CountsAsNormal(double speed)
    {
        using var viewModel = await PlayingAsync(600);

        viewModel.SetSpeed(speed);

        Assert.Equal(Ends(14, 35), viewModel.EndsAtText);
    }

    [Fact]
    public async Task APositionPastTheLength_EndsNow()
    {
        using var viewModel = await PlayingAsync(3000);

        Assert.Equal(Ends(14, 0), viewModel.EndsAtText);
    }

    [Fact]
    public async Task APositionThatIsNotANumber_ShowsNoEnd()
    {
        using var viewModel = await PlayingAsync(600);

        _playback!.TimelineOffsetSeconds = double.NaN;
        At(600);

        Assert.Equal("", viewModel.EndsAtText);
    }

    [Theory]
    [InlineData(86_399.0, true)]
    [InlineData(86_400.0, false)]
    [InlineData(86_401.0, false)]
    public async Task AnEndShows_OnlyLessThanADayAway(double length, bool shows)
    {
        using var viewModel = await PlayingAsync(0, length);

        // 23:59:59 from 14:00 is 13:59:59 the next day: 14:00 to the minute.
        Assert.Equal(shows ? Ends(14, 0) : "", viewModel.EndsAtText);
    }

    [Fact]
    public async Task AnEndAfterTheChangeFromSummerTime_IsInWinterTime()
    {
        // 02:30 summer time in Berlin, half an hour before the clocks go back from 03:00 to 02:00.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)) { Zone = berlin };
        using var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => new FakeServerPlayback(),
            new PlaybackStops(), time);

        await viewModel.StartAsync(PlayRequest.Direct(@"C:\media\film.mkv"));
        At(0, length: 3600);

        // An hour later the clocks say 02:30 again, not 03:30.
        Assert.Equal("Ends " + new DateTime(2026, 10, 25, 2, 30, 0).ToString("t", CultureInfo.CurrentCulture), viewModel.EndsAtText);
    }

    [Fact]
    public async Task AnotherZoneOrTimeFormat_IsWrittenAtTheNextPosition()
    {
        using var viewModel = await PlayingAsync(600);
        var before = CultureInfo.CurrentCulture;
        try
        {
            _time.Zone = TimeZoneInfo.CreateCustomTimeZone("Granary test +3", TimeSpan.FromHours(3), "Granary test +3", "Granary test +3");
            At(601);
            Assert.Equal(Ends(15, 35), viewModel.EndsAtText);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            At(602);
            Assert.Matches(@"^Ends 3:35\sPM$", viewModel.EndsAtText);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}

/// <summary>Tests that set the current culture run on their own, so no other test reads a culture they set.</summary>
[CollectionDefinition(nameof(CurrentCultureChanges), DisableParallelization = true)]
public sealed class CurrentCultureChanges;
