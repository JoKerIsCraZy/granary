using System.Globalization;
using System.Net;
using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The Discord status while the player plays: what it shows, when it changes, and when it goes.</summary>
public sealed class PlayerRichPresenceTests
{
    private const double Length = 3000;

    private static readonly PlaybackMediaInfo Heat = PlaybackMediaInfo.Of(new WatchDetail
    {
        ContentId = TmdbPayloads.HeatId,
        Type = WatchItemTypes.Movie,
        Title = "Heat",
        Year = 1995,
        PosterUrl = "/api/v2/artwork/heat/poster.webp?sig=x",
    }, ScriptedSilo.Base);

    private static readonly PlaybackMediaInfo Secrets = PlaybackMediaInfo.Of(new WatchDetail
    {
        ContentId = "episode-dark-1-3",
        Type = WatchItemTypes.Episode,
        Title = "Secrets",
        SeriesId = TmdbPayloads.DarkId,
        SeriesTitle = "Dark",
        SeasonNumber = 1,
        EpisodeNumber = 3,
        PosterUrl = "/api/v2/artwork/dark/still.webp?sig=y",
    }, ScriptedSilo.Base);

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeRichPresence _presence = new();
    private readonly InMemoryPlaybackDevicePreferences _preferences = new() { DiscordStatus = true };
    private readonly ScriptedSilo _silo = new();
    private readonly TmdbTitleLookup _titles;
    private double _position;
    // What the server playback the player makes next plays, and its length (null: the plan names none).
    private PlaybackMediaInfo _next = PlaybackMediaInfo.None;
    private double? _nextLength = Length;

    public PlayerRichPresenceTests()
    {
        TmdbPayloads.Answer(_silo);
        var session = new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
        _titles = new TmdbTitleLookup(new CatalogApi(_silo.Http), new RequestsApi(_silo.Http), session, new LogSink().For<TmdbTitleLookup>());
    }

    private FakeServerPlayback Playback => _playbacks[^1];

    private DateTimeOffset Now => _time.GetUtcNow();

    private PlayerViewModel NewPlayer(PlaybackMediaInfo info)
    {
        _next = info;
        return new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { DurationSeconds = _nextLength, MediaInfo = _next };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
    }

    private PlayerRichPresence Follow(PlayerViewModel player) => new(player, _presence, _preferences, _titles, new ImmediateDispatcher(), _time);

    // A server title playing at `at` seconds. The server names it while it loads, so its TMDB entry is read by the time it
    // plays, and the status shows once.
    private async Task<(PlayerViewModel Player, PlayerRichPresence Status)> PlayingAsync(PlaybackMediaInfo info, double at = 600)
    {
        var player = NewPlayer(info);
        var status = Follow(player);
        await player.StartAsync(PlayRequest.Resume(info.CatalogId ?? "item-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        await status.Lookup;
        At(at);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return (player, status);
    }

    private void At(double seconds, bool paused = false, double length = Length)
    {
        _position = seconds;
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(length), null, HasStarted: true));
    }

    // The player goes on to `info` (the next episode, another title of the room): it loads while the engine still reports
    // where the title before was, and the server says it plays before the player has read the new stream's state.
    private async Task SwitchAsync(PlayerViewModel player, PlaybackMediaInfo info, PlayerRichPresence status, bool leftoverPause = false)
    {
        _next = info;
        await player.StartAsync(PlayRequest.Resume(info.CatalogId ?? "item-2"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        await status.Lookup;
        _time.Advance(PlayerRichPresence.MinimumSpacing);
        _engine.RaiseState(new PlaybackSnapshot(false, leftoverPause, TimeSpan.FromSeconds(_position), TimeSpan.FromSeconds(Length), null));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
    }

    // Playback goes on for a while, reported as mpv does, four times a second: long enough for Discord to take another change.
    private void PlayOn(double seconds = 5)
    {
        for (double played = 0; played < seconds; played += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            At(_position + 0.25);
        }
    }

    private (DateTimeOffset Start, DateTimeOffset End) Timeline(double position, double length = Length, double speed = 1)
    {
        var start = Now - TimeSpan.FromSeconds(position / speed);
        return (start, start + TimeSpan.FromSeconds(length / speed));
    }

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    [Fact]
    public async Task AnEpisode_ShowsItsSeries_OverItsCodeAndTitle_WithThePosterAndTheSeriesOnTmdb()
    {
        await PlayingAsync(Secrets);

        var (start, end) = Timeline(600);
        Assert.Equal(new RichPresenceState("Dark", "S01E03 · Secrets", start, end, TmdbPayloads.DarkPoster, TmdbPayloads.DarkPage), _presence.Current);
        Assert.Single(_presence.Shown);
    }

    [Fact]
    public async Task AMovie_ShowsItsTitle_OverItsYear_WithThePosterAndTheMovieOnTmdb()
    {
        await PlayingAsync(Heat);

        var (start, end) = Timeline(600);
        Assert.Equal(new RichPresenceState("Heat", "1995", start, end, TmdbPayloads.HeatPoster, TmdbPayloads.HeatPage), _presence.Current);
    }

    [Fact]
    public async Task AMovieWithoutAYear_HasNoSecondLine()
    {
        await PlayingAsync(Heat with { Year = null, Subtitle = "" });

        Assert.Equal(("Heat", null), (_presence.Current!.Details, _presence.Current.State));
    }

    [Fact]
    public async Task AnEpisodeWithoutATitle_ShowsItsCodeAlone_WithNumbersPastNinetyNineAsTheyAre()
    {
        await PlayingAsync(Secrets with { EpisodeTitle = null, SeasonNumber = 2, EpisodeNumber = 104 });

        Assert.Equal(("Dark", "S02E104"), (_presence.Current!.Details, _presence.Current.State));
    }

    [Fact]
    public async Task AnEpisodeWithoutItsSeries_ShowsItsOwnTitle_OverItsCode()
    {
        await PlayingAsync(Secrets with { SeriesTitle = null });

        Assert.Equal(("Secrets", "S01E03"), (_presence.Current!.Details, _presence.Current.State));
    }

    [Fact]
    public async Task AFilePlayedDirectly_ShowsNoStatus_AsItsNameMayBePrivate()
    {
        var player = NewPlayer(Heat);
        using var status = Follow(player);

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\Holiday.mkv"));
        At(5);
        At(5, paused: true);

        Assert.Equal(PlayerPhase.Playing, player.Phase);
        Assert.Empty(_presence.Shown);
        Assert.Equal(0, _presence.Clears);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AFilePlayedAfterASiloTitle_TakesTheStatusAway()
    {
        var (player, _) = await PlayingAsync(Heat);

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\Holiday.mkv"));
        _time.Advance(PlayerRichPresence.MinimumSpacing);
        At(5);

        Assert.Equal((1, null), (_presence.Clears, _presence.Current));
        Assert.Single(_presence.Shown);
    }

    [Fact]
    public async Task ASiloTitleOfAnotherKind_ShowsByTheTitleThePlayerShows_WithGranarysPicture()
    {
        var extra = PlaybackMediaInfo.Of(new WatchDetail { ContentId = "extra-1", Type = "extra", Title = "Making of Heat", Year = 1995 }, ScriptedSilo.Base);

        await PlayingAsync(extra);

        var (start, end) = Timeline(600);
        Assert.Equal(new RichPresenceState("Making of Heat", null, start, end, null, null), _presence.Current);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task ATitleWithoutAName_ShowsNoStatus()
    {
        await PlayingAsync(Heat with { Title = " " });

        Assert.Empty(_presence.Shown);
    }

    [Fact]
    public async Task WhileATitleLoads_NothingShows()
    {
        var player = NewPlayer(Heat);
        using var status = Follow(player);

        await player.StartAsync(PlayRequest.Resume(TmdbPayloads.HeatId));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        await status.Lookup;

        Assert.Empty(_presence.Shown);
        Assert.Equal(0, _presence.Clears);
    }

    [Fact]
    public async Task AsItPlays_TheTimelineRunsOnInDiscord_SoPositionTicksSendNothing()
    {
        await PlayingAsync(Heat);

        for (int tick = 0; tick < 40; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            // As mpv reports it: a little early or late now and then.
            At(_position + (tick % 3 == 0 ? 0.4 : 0.2));
        }

        Assert.Single(_presence.Shown);
    }

    [Fact]
    public async Task ASeek_MovesTheTimelineAtOnce()
    {
        await PlayingAsync(Heat);
        PlayOn();

        At(1800);

        Assert.Equal(2, _presence.Shown.Count);
        Assert.Equal(Timeline(1800), (_presence.Current!.StartedAt!.Value, _presence.Current.EndsAt!.Value));
    }

    [Fact]
    public async Task APause_SaysPaused_WithoutATimeline_AndPlayingOnRunsItAgain()
    {
        await PlayingAsync(Secrets);
        PlayOn();

        At(_position, paused: true);
        Assert.Equal(("S01E03 · Secrets · Paused", null, null), (_presence.Current!.State, _presence.Current.StartedAt, _presence.Current.EndsAt));

        _time.Advance(TimeSpan.FromMinutes(2));
        At(_position);

        var (start, end) = Timeline(_position);
        Assert.Equal(("S01E03 · Secrets", start, end), (_presence.Current!.State, _presence.Current.StartedAt!.Value, _presence.Current.EndsAt!.Value));
        Assert.Equal(3, _presence.Shown.Count);
    }

    [Fact]
    public async Task APausedTitleWithoutASecondLine_SaysJustPaused()
    {
        await PlayingAsync(Heat with { Year = null, Subtitle = "" });
        PlayOn();

        At(_position, paused: true);

        Assert.Equal(("Heat", "Paused"), (_presence.Current!.Details, _presence.Current.State));
    }

    [Fact]
    public async Task ANewTitle_ShowsOnceItsOwnStreamPlays_NotWithThePositionAndLengthLeftByTheTitleBefore()
    {
        var (player, status) = await PlayingAsync(Heat, at: 2950);
        _nextLength = null;

        await SwitchAsync(player, Secrets, status);

        Assert.Null(_presence.Current);
        Assert.Single(_presence.Shown);

        At(30, length: 2400);

        var (start, end) = Timeline(30, 2400);
        Assert.Equal(new RichPresenceState("Dark", "S01E03 · Secrets", start, end, TmdbPayloads.DarkPoster, TmdbPayloads.DarkPage), _presence.Current);
        Assert.Equal(2, _presence.Shown.Count);
    }

    [Fact]
    public async Task ANewTitle_IsNotShownPaused_ForThePauseLeftByTheTitleBefore()
    {
        var (player, status) = await PlayingAsync(Heat);
        PlayOn();
        At(_position, paused: true);

        await SwitchAsync(player, Secrets, status, leftoverPause: true);

        Assert.Null(_presence.Current);
        Assert.DoesNotContain(_presence.Shown, shown => shown.Details == "Dark");

        At(0);

        Assert.Equal(("Dark", "S01E03 · Secrets", Timeline(0).Start), (_presence.Current!.Details, _presence.Current.State, _presence.Current.StartedAt!.Value));
    }

    [Fact]
    public async Task ALateAnswerForTheTitleBefore_LeavesTheNewTitlesPosterAlone()
    {
        var heatAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, async () =>
        {
            await heatAnswer.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail)();
        });
        var player = NewPlayer(Heat);
        using var status = Follow(player);
        await player.StartAsync(PlayRequest.Resume(TmdbPayloads.HeatId));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        var heatLookup = status.Lookup;
        At(600);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        await SwitchAsync(player, Secrets, status);
        At(0);
        Assert.Equal(TmdbPayloads.DarkPoster, _presence.Current!.Poster);
        int shown = _presence.Shown.Count;

        heatAnswer.SetResult();
        await heatLookup;
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        Assert.Equal(shown, _presence.Shown.Count);
        Assert.Equal(TmdbPayloads.DarkPoster, _presence.Current!.Poster);
        Assert.DoesNotContain(_presence.Shown, state => state.Poster == TmdbPayloads.HeatPoster);
    }

    [Fact]
    public async Task ALengthNotKnownYet_ShowsNoTimeline_UntilItIs()
    {
        var player = NewPlayer(Heat);
        using var status = Follow(player);
        await player.StartAsync(PlayRequest.Resume(TmdbPayloads.HeatId));
        Playback.DurationSeconds = null;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        await status.Lookup;
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(600), null, null, HasStarted: true));
        _position = 600;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.Equal(("Heat", null, null), (_presence.Current!.Details, _presence.Current.StartedAt, _presence.Current.EndsAt));

        _time.Advance(PlayerRichPresence.MinimumSpacing);
        At(_position + PlayerRichPresence.MinimumSpacing.TotalSeconds);

        Assert.Equal(Timeline(_position), (_presence.Current!.StartedAt!.Value, _presence.Current.EndsAt!.Value));
        Assert.Equal(2, _presence.Shown.Count);
    }

    [Fact]
    public async Task AtAnotherSpeed_TheTimelineRunsInRealTime_AndPositionTicksStillSendNothing()
    {
        var (player, _) = await PlayingAsync(Heat);
        PlayOn();

        player.SetSpeed(2);

        Assert.Equal(Timeline(_position, speed: 2), (_presence.Current!.StartedAt!.Value, _presence.Current.EndsAt!.Value));
        int shown = _presence.Shown.Count;
        for (int tick = 0; tick < 10; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            At(_position + 2);
        }
        Assert.Equal(shown, _presence.Shown.Count);
    }

    [Fact]
    public async Task ChangesInQuickSuccession_ReachDiscordAtMostEveryFourSeconds_TheLatestOnly()
    {
        await PlayingAsync(Heat);
        PlayOn();
        At(_position, paused: true);
        int shown = _presence.Shown.Count;

        _time.Advance(TimeSpan.FromSeconds(1));
        At(_position);
        At(1200);
        Assert.Equal(shown, _presence.Shown.Count);

        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(shown + 1, _presence.Shown.Count);
        Assert.Equal(("1995", Timeline(1200).Start), (_presence.Current!.State, _presence.Current.StartedAt!.Value));
    }

    [Fact]
    public async Task APauseUndoneBeforeDiscordMayTakeIt_SendsNothing()
    {
        await PlayingAsync(Heat);
        int shown = _presence.Shown.Count;

        At(_position, paused: true);
        At(_position);
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        Assert.Equal(shown, _presence.Shown.Count);
    }

    [Fact]
    public async Task LeavingThePlayer_TakesTheStatusAwayAtOnce_AndOnlyOnce()
    {
        var (player, status) = await PlayingAsync(Secrets);

        player.Leave();
        Assert.Equal((1, null), (_presence.Clears, _presence.Current));

        status.Dispose();
        Assert.Equal(1, _presence.Clears);
    }

    [Fact]
    public async Task AChangeWaitingForDiscord_IsDropped_WhenThePlayerIsLeft()
    {
        var (player, _) = await PlayingAsync(Heat);
        At(_position, paused: true);

        player.Leave();
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        Assert.Single(_presence.Shown);
        Assert.Null(_presence.Current);
    }

    [Fact]
    public async Task ClosingThePlayerPage_TakesTheStatusAway_AndTheStatusFollowsTheTitleNoMore()
    {
        var (_, status) = await PlayingAsync(Heat);

        status.Dispose();
        PlayOn();
        At(_position, paused: true);

        Assert.Equal((1, null), (_presence.Clears, _presence.Current));
        Assert.Single(_presence.Shown);
    }

    [Theory]
    [InlineData(ServerPlaybackPhase.Failed)]
    [InlineData(ServerPlaybackPhase.Preparing)]
    public async Task ATitleThatFailedOrIsLoadingAgain_IsNoStatus(ServerPlaybackPhase phase)
    {
        await PlayingAsync(Secrets);

        Playback.Raise(new ServerPlaybackStatus(phase, "The server stopped answering.", CanRetry: true));

        Assert.Equal((1, null), (_presence.Clears, _presence.Current));
    }

    [Fact]
    public async Task TurnedOff_TheStatusGoesAtOnce_AndTurnedOnItComesBack()
    {
        await PlayingAsync(Heat);

        await _preferences.SetDiscordStatusAsync(false);
        Assert.Equal((1, null), (_presence.Clears, _presence.Current));
        PlayOn();
        Assert.Null(_presence.Current);

        await _preferences.SetDiscordStatusAsync(true);

        Assert.Equal(("Heat", Timeline(_position).Start, TmdbPayloads.HeatPoster), (_presence.Current!.Details, _presence.Current.StartedAt!.Value, _presence.Current.Poster));
        Assert.Single(_silo.SentTo(HttpMethod.Get, TmdbPayloads.HeatItemPath));
    }

    [Fact]
    public async Task WhileTheSettingIsOff_NothingShows_AndNothingIsLookedUp_UntilItIsOn()
    {
        await _preferences.SetDiscordStatusAsync(false);
        var (_, status) = await PlayingAsync(Heat);
        PlayOn();
        At(_position, paused: true);

        Assert.Empty(_presence.Shown);
        Assert.Equal(0, _presence.Clears);
        Assert.Empty(_silo.Sent);

        await _preferences.SetDiscordStatusAsync(true);
        await status.Lookup;
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        Assert.Equal(("Heat", "1995 · Paused", TmdbPayloads.HeatPoster), (_presence.Current!.Details, _presence.Current.State, _presence.Current.Poster));
    }

    [Fact]
    public async Task ATitlePlayedAgain_IsNotLookedUpAgain()
    {
        var (first, status) = await PlayingAsync(Heat);
        first.Dispose();
        status.Dispose();
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        await PlayingAsync(Heat);

        Assert.Equal(TmdbPayloads.HeatPoster, _presence.Current!.Poster);
        Assert.Equal((1, 1), (_silo.SentTo(HttpMethod.Get, TmdbPayloads.HeatItemPath).Count, _silo.SentTo(HttpMethod.Get, TmdbPayloads.HeatDetailPath).Count));
    }

    [Fact]
    public async Task WithRequestsOff_GranarysPictureShows_AndNothingOfTheServerIsPassedOn()
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, ScriptedSilo.Problem(409, "capability_disabled"));

        await PlayingAsync(Heat);
        PlayOn();
        At(_position, paused: true);

        Assert.Equal((null, TmdbPayloads.HeatPage), (_presence.Current!.Poster, _presence.Current.Link));
        Assert.NotNull(Heat.Artwork);
        Assert.All(_presence.Shown, shown => Assert.DoesNotContain(ScriptedSilo.Base.Host, shown.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task APosterArrivingLate_ShowsAsSoonAsDiscordMayTakeIt()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, async () =>
        {
            await answer.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail)();
        });
        var player = NewPlayer(Heat);
        using var status = Follow(player);
        await player.StartAsync(PlayRequest.Resume(TmdbPayloads.HeatId));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        At(600);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.Equal(("Heat", null), (_presence.Current!.Details, _presence.Current.Poster));

        answer.SetResult();
        await status.Lookup;
        Assert.Single(_presence.Shown);
        _time.Advance(PlayerRichPresence.MinimumSpacing);

        Assert.Equal((2, TmdbPayloads.HeatPoster), (_presence.Shown.Count, _presence.Current!.Poster));
    }

    [Fact]
    public async Task LongTexts_AreCutToWhatDiscordTakes_WithoutBreakingACharacter()
    {
        string series = string.Concat(Enumerable.Repeat("Ä", 100));
        string episode = string.Concat(Enumerable.Repeat("👩‍🚀", 20));

        await PlayingAsync(Secrets with { SeriesTitle = series, EpisodeTitle = episode });

        var shown = _presence.Current!;
        Assert.InRange(Bytes(shown.Details), 2, 128);
        Assert.InRange(Bytes(shown.State!), 2, 128);
        Assert.StartsWith("ÄÄÄ", shown.Details, StringComparison.Ordinal);
        Assert.EndsWith("…", shown.Details, StringComparison.Ordinal);
        Assert.StartsWith("S01E03 · 👩‍🚀", shown.State, StringComparison.Ordinal);
        Assert.EndsWith("…", shown.State, StringComparison.Ordinal);
        var elements = StringInfo.GetTextElementEnumerator(shown.State![..^1]["S01E03 · ".Length..]);
        while (elements.MoveNext())
        {
            Assert.Equal("👩‍🚀", elements.GetTextElement());
        }
    }

    [Fact]
    public async Task AOneLetterTitle_IsPadded_SoDiscordTakesIt()
    {
        await PlayingAsync(Heat with { Title = "M", Year = 1931, CatalogId = null });

        var details = _presence.Current!.Details;
        Assert.StartsWith("M", details, StringComparison.Ordinal);
        Assert.True(details.Trim().Length >= 2);
    }
}
