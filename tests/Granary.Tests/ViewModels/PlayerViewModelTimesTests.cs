using System.Globalization;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The times under the player's title: the duration or the time left, as this PC chose last, and the clock time the
/// title ends.
/// </summary>
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
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        _playback!.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(position, paused);
        return viewModel;
    }

    private void At(double position, bool paused = false, double? length = null) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(position),
            length is { } seconds ? TimeSpan.FromSeconds(seconds) : null, null, HasStarted: true));

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
}
