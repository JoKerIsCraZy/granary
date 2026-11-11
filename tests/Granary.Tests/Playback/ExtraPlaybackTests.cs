using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A title's local extra plays as any title of the server does, from its start as the Android and Apple apps play it. The
/// server's watch detail names a series' extra with its series, but an extra is no episode: nothing plays next after it, as
/// in the apps, whose next episode needs a season and an episode number.
/// </summary>
public sealed class ExtraPlaybackTests
{
    private const string ExtraId = "extra-71";
    private const string SeriesId = "series-tvdb-296762";

    private readonly ServerPlaybackFixture _fixture = new();

    // The watch detail of a series' extra: the movie's payload, whose profile left it at 1325.5 s, named an extra.
    public ExtraPlaybackTests()
    {
        string extra = PlaybackPayloads.WatchStateOk
            .Replace("\"content_id\": \"movie:heat-1995\"", "\"content_id\": \"" + ExtraId + "\"", StringComparison.Ordinal)
            .Replace("\"type\": \"movie\"", "\"type\": \"extra\", \"series_id\": \"" + SeriesId + "\", \"series_title\": \"Severance\"",
                StringComparison.Ordinal);
        Assert.Contains("\"type\": \"extra\"", extra, StringComparison.Ordinal);
        Assert.Contains("\"position_seconds\": 1325.5", extra, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + ExtraId, Ok(extra));
    }

    [Fact]
    public async Task ASeriesExtra_IsNoEpisode_SoNothingPlaysNextAfterIt()
    {
        var playback = await _fixture.StartAsync(PlayRequest.FromStart(ExtraId));

        Assert.Single(_fixture.Engine.Loaded);
        Assert.Null(playback.Episode);
        Assert.Equal(("Heat", "1995"), (playback.Title, playback.Subtitle));
        Assert.Equal(PlaybackMediaKind.Other, playback.MediaInfo.Kind);
    }

    [Fact]
    public async Task AnExtraLeftHalfWay_StartsFromItsBeginning_NotWhereTheProfileLeftIt()
    {
        // The server plans what it was asked for: from 0.
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds
            .Replace("\"source_start_seconds\": 12.5", "\"source_start_seconds\": 0", StringComparison.Ordinal)
            .Replace("\"player_start_seconds\": 12.5", "\"player_start_seconds\": 0", StringComparison.Ordinal)));

        await _fixture.StartAsync(PlayRequest.FromStart(ExtraId));

        // Resumed, it would ask for the 1325.5 s the profile left.
        Assert.Equal(0, _fixture.Body(HttpMethod.Post, StartPath).GetProperty("start_position").GetDouble());
        Assert.Equal(TimeSpan.Zero, Assert.Single(_fixture.Engine.Loaded).Start);
    }

    [Fact]
    public async Task AnExtrasEnd_ShowsNoNextUp_AndLeavesThePlayer()
    {
        var navigation = new RecordingNavigation();
        var upNext = new AskedUpNext();
        using var player = new PlayerViewModel(_fixture.Engine, new ImmediateDispatcher(), navigation, _fixture.Create, new PlaybackStops(),
            _fixture.Time, upNext);
        navigation.NavigateTo(AppRoute.Item, SeriesId);
        navigation.NavigateTo(AppRoute.Player);
        await player.StartAsync(PlayRequest.FromStart(ExtraId));

        // It plays on through the credits (10000 s) and the last 30 s, where an episode shows its card and Playing Next.
        _fixture.Engine.RaiseState(Frame(12.5));
        for (double seconds = 9990; seconds <= 10199; seconds += 1)
        {
            _fixture.Time.Advance(TimeSpan.FromSeconds(1));
            _fixture.Engine.RaiseState(Frame(seconds));
            Assert.False(player.IsNextUpCardShown, $"the card at {seconds} s");
            Assert.False(player.IsNextUpScreenShown, $"the screen at {seconds} s");
        }
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await Until(() => navigation.PlayerLeaves == 1, "the player left at the end");

        Assert.False(player.IsNextUpScreenShown);
        Assert.False(player.IsNextUpCountingDown);
        Assert.Equal(0, upNext.EpisodesAsked);
    }

    // Counts what the player asks about the next episode; the profile's On Deck is empty.
    private sealed class AskedUpNext : IUpNextSource
    {
        public int EpisodesAsked { get; private set; }

        public Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
        {
            EpisodesAsked++;
            return Task.FromResult<NextEpisode?>(null);
        }

        public Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
        {
            EpisodesAsked++;
            return Task.FromResult<NextEpisode?>(null);
        }

        public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaCard>>([]);
    }
}
