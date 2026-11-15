using Granary.Core.ViewModels.Detail;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>The scores under a title's facts, as the web's ScoreRow on its movie, series and episode pages.</summary>
public sealed class ItemDetailScoreRowTests
{
    private readonly ItemDetailFixture _fixture = new();

    [Fact]
    public async Task AMovie_ShowsItsScores_InTheServersOrder()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.True(detail.HasRatings);
        Assert.Equal([new DetailRating("IMDb", "5.4", "imdb"), new DetailRating("TMDB", "7.1", "tmdb")], detail.Ratings);
    }

    [Fact]
    public async Task ASeries_ShowsEveryScoreTheServerChose()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        Assert.Equal(["IMDb 7.5", "TMDB 7.7", "Rotten Tomatoes 98%", "Metacritic 75"], detail.Ratings.Select(r => r.AccessibleName));
    }

    [Fact]
    public async Task AnEpisode_ShowsItsOwnScores()
    {
        var detail = await _fixture.Episode().LoadAsync(EpisodeId);

        Assert.Equal([new DetailRating("IMDb", "8.1", "imdb")], detail.Ratings);
    }

    [Fact]
    public async Task TMDBsScore_IsDrawnWithItsLogo_AndEveryOtherSourceByItsName()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        // As the web's RatingEntry: TMDB's approved logo, the other sources' marks as text.
        Assert.Equal([false, true, false, false], detail.Ratings.Select(r => r.ShowsLogo));
        Assert.Equal([true, false, true, true], detail.Ratings.Select(r => r.ShowsMark));
        Assert.Equal("TMDB 7.7", detail.Ratings[1].AccessibleName);
    }

    [Fact]
    public async Task ASeason_ShowsNoScores()
    {
        string rated = Season.Replace("\"episode_count\": 8,",
            "\"episode_count\": 8, \"ratings\": [{ \"source\": \"tmdb\", \"name\": \"TMDB\", \"score\": 80, \"display\": \"8.0\" }],",
            StringComparison.Ordinal);
        var detail = await _fixture.Season().Get(ItemPath(Season3Id), Ok(rated)).LoadAsync(Season3Id);

        Assert.False(detail.HasRatings);
        Assert.Empty(detail.Ratings);
    }

    [Fact]
    public async Task ATitleWithoutScores_ShowsNoRow()
    {
        string unrated = Movie.Replace("{ \"source\": \"imdb\", \"name\": \"IMDb\", \"score\": 54, \"display\": \"5.4\" },", "", StringComparison.Ordinal)
            .Replace("{ \"source\": \"tmdb\", \"name\": \"TMDB\", \"score\": 71, \"display\": \"7.1\" }", "", StringComparison.Ordinal);
        var detail = await _fixture.Movie().Get(ItemPath(MovieId), Ok(unrated)).LoadAsync(MovieId);

        Assert.False(detail.HasRatings);
    }
}
