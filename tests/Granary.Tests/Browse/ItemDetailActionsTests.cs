using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>Watched, favourite and watchlist change at once and roll back when the server refuses (docs/api-contract.md, 7.11).</summary>
public sealed class ItemDetailActionsTests
{
    private readonly ItemDetailFixture _fixture = new();

    private static string Watched(string id) => "api/v2/watched/" + id;

    [Fact]
    public async Task MarkWatched_ChangesAtOnceAndClearsTheResumePoint()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Post, Watched(MovieId), held);
        var detail = await _fixture.LoadAsync(MovieId);

        var marking = detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.True(detail.IsWatched);
        Assert.Equal("Mark Unwatched", detail.WatchedLabel);
        Assert.Equal("Play", detail.PlayLabel);
        gate.SetResult(NoContentResponse());
        await marking;
        Assert.True(detail.IsWatched);
        Assert.False(detail.HasActionError);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, Watched(MovieId)));
    }

    [Fact]
    public async Task MarkWatched_Refused_RollsBackOnceAndExplains()
    {
        _fixture.Movie().Silo.On(HttpMethod.Post, Watched(MovieId), ScriptedSilo.Problem(500, "internal"));
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.False(detail.IsWatched);
        Assert.Equal("Mark Watched", detail.WatchedLabel);
        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal("Could not mark this as watched (error 500). Try again.", detail.ActionError);
        Assert.True(detail.HasActionError);
        // Non-retryable: one attempt only.
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, Watched(MovieId)));
        Assert.Contains(_fixture.Logs.Lines, l => l.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkUnwatched_DeletesTheWatchedState()
    {
        _fixture.Episode().Silo.On(HttpMethod.Delete, Watched(EpisodeId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(EpisodeId);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.False(detail.IsWatched);
        Assert.Equal("Mark Watched", detail.WatchedLabel);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Delete, Watched(EpisodeId)));
    }

    [Fact]
    public async Task MarkSeasonWatched_MarksEveryEpisodeCard()
    {
        _fixture.Season().Silo.On(HttpMethod.Post, Watched(Season3Id), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(Season3Id);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal("Mark Season Unwatched", detail.WatchedLabel);
        Assert.All(detail.Episodes, e => Assert.True(e.Card.IsWatched));
        Assert.All(detail.Episodes, e => Assert.Null(e.Card.Progress));
        Assert.Equal("Play First Episode", detail.PlayLabel);
    }

    [Fact]
    public async Task MarkSeasonWatched_Refused_RestoresTheEpisodeCards()
    {
        _fixture.Season().Silo.On(HttpMethod.Post, Watched(Season3Id), ScriptedSilo.NetworkError());
        var detail = await _fixture.LoadAsync(Season3Id);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.False(detail.IsWatched);
        Assert.Equal([true, false], detail.Episodes.Select(e => e.Card.IsWatched));
        Assert.Equal(0.5, detail.Episodes[1].Card.Progress);
        Assert.Equal("Could not mark this as watched. Check the connection and try again.", detail.ActionError);
    }

    [Fact]
    public async Task MarkSeriesWatched_MarksEverySeasonCardAndRollsBackOnFailure()
    {
        var (held, gate) = Held();
        _fixture.Series().Silo.On(HttpMethod.Post, Watched(SeriesId), held);
        var detail = await _fixture.LoadAsync(SeriesId);

        var marking = detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.All(detail.Seasons, s => Assert.True(s.IsWatched));
        Assert.Equal("8 episodes", detail.Seasons[2].Caption);
        Assert.Null(detail.Seasons[2].Progress);
        Assert.Equal("Play", detail.PlayLabel);

        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        await marking;

        Assert.False(detail.IsWatched);
        Assert.Equal("4 of 8 episodes", detail.Seasons[2].Caption);
        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal("Could not mark this as watched. The server is busy, try again.", detail.ActionError);
    }

    [Fact]
    public async Task Favorite_RemovesAndRestoresWhenRefused()
    {
        _fixture.Series().Silo.On(HttpMethod.Delete, "api/v2/favorites/" + SeriesId, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(SeriesId);

        await detail.ToggleFavoriteCommand.ExecuteAsync(null);
        Assert.False(detail.IsFavorite);
        Assert.Equal("Add to favorites", detail.FavoriteLabel);

        _fixture.Silo.On(HttpMethod.Put, "api/v2/favorites/" + SeriesId, ScriptedSilo.NetworkError());
        await detail.ToggleFavoriteCommand.ExecuteAsync(null);

        Assert.False(detail.IsFavorite);
        Assert.Equal("Could not add this to your favorites. Check the connection and try again.", detail.ActionError);
    }

    [Fact]
    public async Task Watchlist_AddsAndRollsBackWhenRateLimited()
    {
        _fixture.Movie().Silo.On(HttpMethod.Put, "api/v2/watchlist/" + MovieId, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.ToggleWatchlistCommand.ExecuteAsync(null);
        Assert.True(detail.IsInWatchlist);
        Assert.Equal("Remove from Watchlist", detail.WatchlistLabel);

        _fixture.Silo.On(HttpMethod.Delete, "api/v2/watchlist/" + MovieId, ScriptedSilo.Problem(429, "rate_limited", TimeSpan.FromSeconds(90)));
        await detail.ToggleWatchlistCommand.ExecuteAsync(null);

        Assert.True(detail.IsInWatchlist);
        Assert.Equal("Could not remove this from your watchlist. The server is receiving too many requests. Try again in 2 minutes.", detail.ActionError);
    }

    [Fact]
    public async Task DismissingTheError_ClearsIt()
    {
        _fixture.Movie().Silo.On(HttpMethod.Put, "api/v2/favorites/" + MovieId, ScriptedSilo.NetworkError());
        var detail = await _fixture.LoadAsync(MovieId);
        await detail.ToggleFavoriteCommand.ExecuteAsync(null);
        Assert.True(detail.HasActionError);

        detail.DismissActionError();

        Assert.False(detail.HasActionError);
    }

    [Fact]
    public async Task SeasonsAndEpisodes_HaveNoFavoriteOrWatchlist()
    {
        var detail = await _fixture.Season().LoadAsync(Season3Id);

        await detail.ToggleFavoriteCommand.ExecuteAsync(null);
        await detail.ToggleWatchlistCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Method != HttpMethod.Get);
    }
}
