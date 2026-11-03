using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>The active filters as the web's removable badges (<c>catalogFilterBadges.ts</c>), Granary's own facets beside them.</summary>
public sealed class LibraryFilterBadgesTests
{
    private static readonly LibraryFilterSet Everything = new()
    {
        Genres = ["Crime", "Drama"], Decades = [1990], YearFrom = 1980, YearTo = 2000, ContentRatings = ["PG-13", "R"], FourK = true, Hdr = true,
        DolbyVision = true, OriginalLanguages = ["fr"], AudioLanguages = ["de"], SubtitleLanguages = ["en"], WatchStatus = LibraryWatchStatus.InProgress,
    };

    [Fact]
    public void Badges_AreTheWebsLabels_InTheFlyoutsOrder()
    {
        var badges = LibraryFilterBadges.For(Everything, Name);

        Assert.Equal(
        [
            "Genre: Crime", "Genre: Drama", "Decade: 1990s", "Year: 1980–2000", "Rated: PG-13", "Rated: R", "Language: French", "Audio: German",
            "Subtitles: English", "Watch: in progress", "4K", "HDR", "DOVI",
        ], badges.Select(b => b.Label));
        Assert.Equal("Remove Genre: Crime", badges[0].RemoveName);
        Assert.Equal(Everything.Count, badges.Count);
    }

    [Theory]
    [InlineData(1980, null, "Year: >= 1980")]
    [InlineData(null, 2000, "Year: <= 2000")]
    [InlineData(1980, 1989, "Year: 1980–1989")]
    public void TheYearRange_IsOneBadge(int? from, int? to, string label)
    {
        Assert.Equal([label], LibraryFilterBadges.For(new LibraryFilterSet { YearFrom = from, YearTo = to }, Name).Select(b => b.Label));
    }

    [Theory]
    [InlineData(LibraryWatchStatus.Unwatched, "Watch: unwatched")]
    [InlineData(LibraryWatchStatus.InProgress, "Watch: in progress")]
    [InlineData(LibraryWatchStatus.Watched, "Watch: watched")]
    public void TheWatchStatus_IsWordedAsTheWebWordsIt(LibraryWatchStatus status, string label)
    {
        Assert.Equal([label], LibraryFilterBadges.For(new LibraryFilterSet { WatchStatus = status }, Name).Select(b => b.Label));
    }

    [Fact]
    public void NoFilters_NoBadges()
    {
        Assert.Empty(LibraryFilterBadges.For(LibraryFilterSet.None, Name));
    }

    [Fact]
    public void RemovingABadge_RemovesExactlyItsPiece()
    {
        var badges = LibraryFilterBadges.For(Everything, Name);

        foreach (var badge in badges)
        {
            var left = LibraryFilterBadges.Remove(Everything, badge);

            Assert.Equal(badges.Where(b => b != badge).Select(b => b.Label), LibraryFilterBadges.For(left, Name).Select(b => b.Label));
        }
    }

    [Fact]
    public void RemovingTheYearBadge_ClearsBothEnds_AndTheWatchBadge_TheStatus()
    {
        var badges = LibraryFilterBadges.For(Everything, Name);

        var withoutYears = LibraryFilterBadges.Remove(Everything, badges.Single(b => b.Kind == LibraryFilterBadgeKind.Year));
        var withoutWatch = LibraryFilterBadges.Remove(Everything, badges.Single(b => b.Kind == LibraryFilterBadgeKind.WatchStatus));
        var withoutCrime = LibraryFilterBadges.Remove(Everything, badges[0]);

        Assert.Equal(Everything with { YearFrom = null, YearTo = null }, withoutYears);
        Assert.Equal(Everything with { WatchStatus = LibraryWatchStatus.Any }, withoutWatch);
        Assert.Equal(Everything with { Genres = ["Drama"] }, withoutCrime);
    }

    [Fact]
    public void ABadgeOfAPieceAlreadyGone_ChangesNothing()
    {
        var crime = LibraryFilterBadges.For(Everything, Name)[0];
        var once = LibraryFilterBadges.Remove(Everything, crime);

        Assert.Equal(once, LibraryFilterBadges.Remove(once, crime));
    }

    private static string Name(string code) => code switch
    {
        "fr" => "French",
        "de" => "German",
        "en" => "English",
        _ => code,
    };
}
