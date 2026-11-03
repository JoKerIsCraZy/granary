using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>The rule groups the Filters flyout's facets become, as Silo's apps build them.</summary>
public sealed class LibraryFilterRulesTests
{
    [Fact]
    public void Genres_AreOneGroup_AnyOfWhichMatches()
    {
        var filters = new LibraryFilterSet { Genres = ["Crime", "Drama"] };

        Assert.Equal(["any[genre contains \"Crime\", genre contains \"Drama\"]"], Render(filters));
    }

    [Fact]
    public void Decades_AreYearRanges_AnyOfWhichMatches()
    {
        var filters = new LibraryFilterSet { Decades = [1990, 2000] };

        Assert.Equal(["any[year between [1990,1999], year between [2000,2009]]"], Render(filters));
    }

    [Fact]
    public void VideoQuality_EveryPickedOneMustMatch()
    {
        var filters = new LibraryFilterSet { FourK = true, Hdr = true, DolbyVision = true };

        Assert.Equal(["all[resolution is \"2160p\", hdr is true, dolby_vision is true]"], Render(filters));
    }

    [Fact]
    public void Languages_AreAGroupPerFacet_AnyLanguageOfWhichMatches()
    {
        var filters = new LibraryFilterSet { OriginalLanguages = ["fr"], AudioLanguages = ["de", "en"], SubtitleLanguages = ["pt-br"] };

        Assert.Equal(
        [
            "any[original_language is \"fr\"]",
            "any[audio_language is \"de\", audio_language is \"en\"]",
            "any[subtitle_language is \"pt-br\"]",
        ], Render(filters));
    }

    [Theory]
    [InlineData(LibraryWatchStatus.Unwatched, "all[watched is false, in_progress is false]")]
    [InlineData(LibraryWatchStatus.InProgress, "all[in_progress is true]")]
    [InlineData(LibraryWatchStatus.Watched, "all[watched is true]")]
    public void WatchStatus_IsOneGroupOfItsRules(LibraryWatchStatus status, string group)
    {
        Assert.Equal([group], Render(new LibraryFilterSet { WatchStatus = status }));
    }

    [Fact]
    public void Groups_ComeInTheFlyoutsOrder()
    {
        var filters = new LibraryFilterSet
        {
            Genres = ["Crime"], Decades = [1980], Hdr = true, OriginalLanguages = ["en"], AudioLanguages = ["en"], SubtitleLanguages = ["en"],
            WatchStatus = LibraryWatchStatus.Watched,
        };

        Assert.Equal(["genre", "year", "hdr", "original_language", "audio_language", "subtitle_language", "watched"],
            LibraryFilterRules.For(filters).Select(g => g.Rules[0].Field));
    }

    [Fact]
    public void YearsAndRatingsAlone_StayTheSimpleFilters_OfTheListing()
    {
        var filters = new LibraryFilterSet { YearFrom = 1990, YearTo = 1999, ContentRatings = ["R"] };

        var query = filters.ApplyTo(new CatalogQuery { LibraryId = "1" });

        Assert.Empty(LibraryFilterRules.For(filters));
        Assert.False(query.UsesRules);
        Assert.Equal((1990, 1999), (query.YearMin, query.YearMax));
        Assert.Equal(["R"], query.ContentRatings);
    }

    [Fact]
    public void AnyOtherFilter_TakesTheListingToTheRuleQuery_WithTheYearsAndRatingsBesideIt()
    {
        var filters = new LibraryFilterSet { Genres = ["Drama"], YearFrom = 1990, ContentRatings = ["R"] };

        var query = filters.ApplyTo(new CatalogQuery { LibraryId = "1" });

        Assert.True(query.UsesRules);
        Assert.Null(query.Genre);
        Assert.Equal(1990, query.YearMin);
        Assert.Equal(["R"], query.ContentRatings);
    }

    [Fact]
    public void Count_IsOnePerValue_OneForTheYearRange_AndOneForAWatchStatus()
    {
        var filters = new LibraryFilterSet
        {
            Genres = ["Crime", "Drama"], Decades = [1990], YearFrom = 1980, YearTo = 2000, ContentRatings = ["R"], FourK = true, Hdr = true,
            OriginalLanguages = ["en"], AudioLanguages = ["de", "en"], SubtitleLanguages = ["en"], WatchStatus = LibraryWatchStatus.Unwatched,
        };

        Assert.Equal(12, filters.Count);
        Assert.Equal(0, LibraryFilterSet.None.Count);
        Assert.Equal(1, new LibraryFilterSet { YearTo = 2000 }.Count);
    }

    [Theory]
    [InlineData(2_147_483_640)]
    [InlineData(9_999_990)]
    [InlineData(10_000)]
    public void Normalized_DropsADecadePastTheLastYear_HoweverLarge(int decade)
    {
        Assert.Empty(new LibraryFilterSet { Decades = [decade] }.Normalized().Decades);
    }

    [Fact]
    public void Normalized_KeepsTheLastDecade() =>
        Assert.Equal([9990], new LibraryFilterSet { Decades = [9990] }.Normalized().Decades);

    [Fact]
    public void Normalized_DropsBlanksAndRepeats_OrdersTheYears_AndKeepsOnlyDecades()
    {
        var filters = new LibraryFilterSet
        {
            Genres = ["Crime", " ", "Crime"], Decades = [1990, 1995, -10, 1990], YearFrom = 20000, YearTo = 1990, ContentRatings = ["", "R"],
            AudioLanguages = ["de", "de"],
        };

        var normalized = filters.Normalized();

        Assert.Equal(["Crime"], normalized.Genres);
        Assert.Equal([1990], normalized.Decades);
        Assert.Equal((1990, LibraryFilterSet.MaximumYear), (normalized.YearFrom, normalized.YearTo));
        Assert.Equal(["R"], normalized.ContentRatings);
        Assert.Equal(["de"], normalized.AudioLanguages);
    }

    [Fact]
    public void Sets_WithTheSameFilters_AreEqual()
    {
        var first = new LibraryFilterSet { Genres = ["Crime"], Decades = [1990], Hdr = true, WatchStatus = LibraryWatchStatus.Watched };
        var second = new LibraryFilterSet { Genres = ["Crime"], Decades = [1990], Hdr = true, WatchStatus = LibraryWatchStatus.Watched };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { Genres = ["Drama"] });
    }

    [Fact]
    public void TheDecades_RunFromThisOneBackToThe1900s()
    {
        var decades = LibraryFilterSet.DecadesUntil(2026);

        Assert.Equal(13, decades.Count);
        Assert.Equal(2020, decades[0]);
        Assert.Equal(1900, decades[^1]);
    }

    private static List<string> Render(LibraryFilterSet filters) =>
        [.. LibraryFilterRules.For(filters).Select(g => $"{g.Match}[{string.Join(", ", g.Rules.Select(r => $"{r.Field} {r.Op} {r.Value.GetRawText()}"))}]")];
}
