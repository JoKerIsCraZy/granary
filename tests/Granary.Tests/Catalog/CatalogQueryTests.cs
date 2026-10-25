using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Tests.Catalog;

public sealed class CatalogQueryTests
{
    private static readonly ImageParameters PosterOnly =
        new ImageSizes(Poster: 342).For(JsonSerializer.Deserialize(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities));

    [Fact]
    public void EmptyQuery_SendsNothing()
    {
        Assert.Equal("", new CatalogQuery().ToQueryString());
    }

    [Fact]
    public void EveryMember_BecomesItsParameterInOrder()
    {
        var query = new CatalogQuery
        {
            Source = CatalogSources.Favorites,
            LibraryId = "12",
            Q = "heat",
            NamePrefix = "H",
            Type = MediaScopes.Movie,
            Genre = "Crime",
            Status = "matched",
            YearMin = 1990,
            YearMax = 1999,
            ContentRatings = ["R", "PG-13"],
            Sort = CatalogSort.Descending(CatalogSortFields.AddedAt),
            Group = "work",
            Limit = 42,
            Cursor = "abc",
            SkipTotal = true,
            Images = PosterOnly,
        };

        Assert.Equal(
            "?source=favorites&library_id=12&q=heat&name_prefix=H&type=movie&genre=Crime&status=matched&year_min=1990&year_max=1999"
            + "&content_rating=R&content_rating=PG-13&sort=-added_at&group=work&limit=42&cursor=abc&skip_total=true&poster_width=342",
            query.ToQueryString());
    }

    [Fact]
    public void Values_AreEscaped()
    {
        var query = new CatalogQuery { Q = "Tom & Jerry/Ä+?=#", Genre = "Sci-Fi & Fantasy", Cursor = "a+b/c=" };

        Assert.Equal("?q=Tom%20%26%20Jerry%2F%C3%84%2B%3F%3D%23&genre=Sci-Fi%20%26%20Fantasy&cursor=a%2Bb%2Fc%3D", query.ToQueryString());
    }

    [Fact]
    public void UnsetAndEmptyValues_AreLeftOut()
    {
        var query = new CatalogQuery { Q = "", Genre = null, ContentRatings = [], SkipTotal = false, Sort = CatalogSort.Ascending(CatalogSortFields.Title) };

        Assert.Equal("?sort=title", query.ToQueryString());
    }

    [Fact]
    public void WithCursor_KeepsEveryOtherParameter()
    {
        var first = new CatalogQuery { LibraryId = "12", Sort = CatalogSort.Descending(CatalogSortFields.AddedAt), Limit = 42, Images = PosterOnly };

        var second = first.WithCursor("next-1");

        Assert.Equal("?library_id=12&sort=-added_at&limit=42&cursor=next-1&poster_width=342", second.ToQueryString());
        Assert.Equal(first, second.WithCursor(null));
        Assert.Null(first.Cursor);
    }

    [Fact]
    public void AtPosition_SeeksFromTheWindowCursor_WithoutCountingAgain()
    {
        var first = new CatalogQuery { LibraryId = "12", Sort = CatalogSort.Ascending(CatalogSortFields.Title), Limit = 60, Images = PosterOnly };

        var page = first.AtPosition("w1", 540);

        Assert.Equal("?library_id=12&sort=title&limit=60&cursor=w1&seek=540&skip_total=true&poster_width=342", page.ToQueryString());
        Assert.Equal("?library_id=12&sort=title&limit=60&cursor=n2&skip_total=true&poster_width=342", page.WithCursor("n2").ToQueryString());
        Assert.Throws<ArgumentException>(() => first.AtPosition("", 60));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(CatalogQuery.MaximumSeek + 1)]
    public void Seek_OutsideTheServersRange_IsRefused(int seek)
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogQuery { Cursor = "w1", Seek = seek }.ToQueryString());

        Assert.Equal(nameof(CatalogQuery.Seek), refused.ParamName);
    }

    [Fact]
    public void HomeSectionListing_TakesPagingAndImages()
    {
        var query = CatalogQuery.ForHomeSection("default-continue-watching") with { Limit = 50, Cursor = "c1", SkipTotal = true, Group = "work", Images = PosterOnly };

        Assert.Equal("?source=section&scope=home&section_id=default-continue-watching&group=work&limit=50&cursor=c1&skip_total=true&poster_width=342",
            query.ToQueryString());
    }

    [Fact]
    public void LibrarySectionListing_NamesItsLibrary()
    {
        Assert.Equal("?source=section&scope=library&section_id=recent&library_id=12", CatalogQuery.ForLibrarySection("12", "recent").ToQueryString());
        Assert.Equal("?source=section&section_id=recent&library_id=12",
            new CatalogQuery { Source = CatalogSources.Section, SectionId = "recent", LibraryId = "12" }.ToQueryString());
    }

    public static TheoryData<string, CatalogQuery> RefusedSectionMembers => new()
    {
        { nameof(CatalogQuery.Q), Home with { Q = "heat" } },
        { nameof(CatalogQuery.NamePrefix), Home with { NamePrefix = "A" } },
        { nameof(CatalogQuery.Type), Home with { Type = MediaScopes.Movie } },
        { nameof(CatalogQuery.Genre), Home with { Genre = "Crime" } },
        { nameof(CatalogQuery.Status), Home with { Status = "matched" } },
        { nameof(CatalogQuery.YearMin), Home with { YearMin = 1990 } },
        { nameof(CatalogQuery.YearMax), Home with { YearMax = 1999 } },
        { nameof(CatalogQuery.ContentRatings), Home with { ContentRatings = ["R"] } },
        { nameof(CatalogQuery.Sort), Home with { Sort = CatalogSort.Ascending(CatalogSortFields.Title) } },
        { nameof(CatalogQuery.LibraryId), Home with { LibraryId = "12" } },
        { nameof(CatalogQuery.Genre), CatalogQuery.ForLibrarySection("12", "recent") with { Genre = "Crime" } },
        { nameof(CatalogQuery.Q), Home with { Source = " Section ", Q = "heat" } },
    };

    [Theory]
    [MemberData(nameof(RefusedSectionMembers))]
    public void SectionListing_RefusesFiltersSortAndOtherIds(string member, CatalogQuery query)
    {
        var refused = Assert.Throws<ArgumentException>(() => query.ToQueryString());

        Assert.Equal(member, refused.ParamName);
    }

    [Fact]
    public void SectionListing_NeedsItsIds()
    {
        Assert.Equal(nameof(CatalogQuery.SectionId),
            Assert.Throws<ArgumentException>(() => new CatalogQuery { Source = CatalogSources.Section, Scope = CatalogScopes.Home }.ToQueryString()).ParamName);
        Assert.Equal(nameof(CatalogQuery.SectionId), Assert.Throws<ArgumentException>(() => (Home with { SectionId = " " }).ToQueryString()).ParamName);
        Assert.Equal(nameof(CatalogQuery.LibraryId),
            Assert.Throws<ArgumentException>(() => new CatalogQuery { Source = CatalogSources.Section, SectionId = "recent" }.ToQueryString()).ParamName);
        Assert.Equal(nameof(CatalogQuery.Scope),
            Assert.Throws<ArgumentException>(() => (Home with { Scope = "profile" }).ToQueryString()).ParamName);
    }

    [Fact]
    public void OtherSources_NeedTheirIds()
    {
        Assert.Equal(nameof(CatalogQuery.PersonId),
            Assert.Throws<ArgumentException>(() => new CatalogQuery { Source = CatalogSources.Person }.ToQueryString()).ParamName);
        Assert.Equal(nameof(CatalogQuery.CollectionId),
            Assert.Throws<ArgumentException>(() => new CatalogQuery { Source = CatalogSources.UserCollection }.ToQueryString()).ParamName);
        Assert.Equal("?source=library_collection&collection_id=9",
            new CatalogQuery { Source = CatalogSources.LibraryCollection, CollectionId = "9" }.ToQueryString());
    }

    [Fact]
    public void SectionListing_IgnoresIdsOfOtherSourcesLikeTheServer()
    {
        Assert.Equal("?source=section&scope=home&section_id=s1&collection_id=3",
            (CatalogQuery.ForHomeSection("s1") with { CollectionId = "3" }).ToQueryString());
    }

    [Fact]
    public void RelevanceOrder_NeedsSearchTextOnTheQuerySource()
    {
        var relevance = new CatalogQuery { Sort = CatalogSort.Descending(CatalogSortFields.Relevance) };

        Assert.Equal(nameof(CatalogQuery.Sort), Assert.Throws<ArgumentException>(() => relevance.ToQueryString()).ParamName);
        Assert.Throws<ArgumentException>(() => (relevance with { Source = CatalogSources.Favorites, Q = "heat" }).ToQueryString());
        Assert.Equal("?q=heat&sort=-relevance", (relevance with { Q = "heat" }).ToQueryString());
    }

    [Fact]
    public void VideoWithEpisodes_IsOnlyForTheQuerySource()
    {
        var query = new CatalogQuery { Q = "lumon", Type = MediaScopes.VideoWithEpisodes };

        Assert.Equal("?q=lumon&type=video_with_episodes", query.ToQueryString());
        Assert.Equal(nameof(CatalogQuery.Type),
            Assert.Throws<ArgumentException>(() => (query with { Source = CatalogSources.Watchlist }).ToQueryString()).ParamName);
    }

    [Fact]
    public void GroupAndYears_AreRefusedOutsideTheirRange()
    {
        Assert.Equal(nameof(CatalogQuery.Group), Assert.Throws<ArgumentException>(() => new CatalogQuery { Group = "series" }.ToQueryString()).ParamName);
        Assert.Equal(nameof(CatalogQuery.YearMax), Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogQuery { YearMax = -1 }.ToQueryString()).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Limit_OutsideOneTo200_IsRefused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogQuery { Limit = limit }.ToQueryString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("-title")]
    [InlineData("title,year")]
    public void Sort_IsOneFieldWithoutItsOwnPrefix(string field)
    {
        Assert.Throws<ArgumentException>(() => new CatalogQuery { Sort = new CatalogSort(field) }.ToQueryString());
    }

    [Theory]
    [InlineData("movies", "?library_id=12&type=movie")]
    [InlineData("Movie", "?library_id=12&type=movie")]
    [InlineData("series", "?library_id=12&type=series")]
    [InlineData("tv", "?library_id=12&type=series")]
    [InlineData("shows", "?library_id=12&type=series")]
    [InlineData("mixed", "?library_id=12")]
    [InlineData("audiobooks", "?library_id=12")]
    public void ForLibrary_MapsTheLibraryTypeToAMediaScope(string libraryType, string expected)
    {
        var library = new Library("12", "Library", libraryType, 1, null);

        Assert.Equal(expected, CatalogQuery.ForLibrary(library).ToQueryString());
    }

    [Fact]
    public void ForPerson_ListsNewestFirst()
    {
        Assert.Equal("?source=person&person_id=7&sort=-year", CatalogQuery.ForPerson("7").ToQueryString());
    }

    private static CatalogQuery Home => CatalogQuery.ForHomeSection("default-continue-watching");
}
