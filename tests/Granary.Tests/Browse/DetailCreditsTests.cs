using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Browse;

/// <summary>The "Cast &amp; Crew" row's groups and separators, as the web's castCrewGroups.ts and CastCarousel.tsx build them.</summary>
public sealed class DetailCreditsTests
{
    private static readonly Uri Server = new("https://silo.test/media");

    private static CatalogItemDetail Item(string type, CrewCredit[] crew, params string[] cast) => new()
    {
        ContentId = "item-1",
        Type = type,
        Title = "Title",
        Crew = crew,
        Cast = [.. cast.Select((name, i) => new CastCredit(name, $"Role {i + 1}", i, $"cast-{i}", null, null))],
    };

    private static CrewCredit Crew(string name, string job, string? personId = null) =>
        new(name, job, personId ?? name.ToLowerInvariant().Replace(' ', '-'), null, null);

    // The row as it reads from left to right: each card's name, and each separator as "| " and its label as shown.
    private static string[] Reads(CatalogItemDetail item) =>
        [.. DetailCredits.Row(DetailCredits.Groups(item, Server)).Select(credit => credit switch
        {
            DetailPerson person => person.Name,
            DetailCreditSeparator separator => "| " + separator.Text,
            _ => throw new InvalidOperationException("Neither a card nor a separator."),
        })];

    [Fact]
    public void AMovie_ReadsDirectorThenWritersThenCast()
    {
        var movie = Item(CatalogItemTypes.Movie,
            [Crew("Jonas Brandt", "Writer"), Crew("Leo Marchetti", "Producer"), Crew("Elena Moreau", "Director"), Crew("Mira Okafor", "Screenplay")],
            "Tomas Lind", "Ines Varga");

        Assert.Equal(["Elena Moreau", "| WRITERS", "Jonas Brandt", "Mira Okafor", "| CAST", "Tomas Lind", "Ines Varga"], Reads(movie));
        Assert.Equal(["Director", "Writer", "Writer", "Role 1", "Role 2"], DetailCredits.Groups(movie, Server).SelectMany(g => g.People).Select(p => p.Role));
    }

    [Fact]
    public void OnlyTheGroupsAfterTheFirst_HaveALabel()
    {
        var movie = Item(CatalogItemTypes.Movie, [Crew("Elena Moreau", "Director"), Crew("Jonas Brandt", "Writer")], "Tomas Lind");

        // The director leads, so "Director" is never a label.
        Assert.Equal([null, "Writers", "Cast"], DetailCredits.Groups(movie, Server).Select(g => g.Label));
    }

    [Fact]
    public void AMovieWithoutWriters_GoesFromItsDirectorToTheCast()
    {
        var movie = Item(CatalogItemTypes.Movie, [Crew("Elena Moreau", "Director"), Crew("Leo Marchetti", "Producer")], "Tomas Lind");

        Assert.Equal(["Elena Moreau", "| CAST", "Tomas Lind"], Reads(movie));
        Assert.Equal([null, "Cast"], DetailCredits.Groups(movie, Server).Select(g => g.Label));
    }

    [Fact]
    public void AMovieWithoutADirector_LeadsWithItsWriters_Unlabelled()
    {
        var movie = Item(CatalogItemTypes.Movie, [Crew("Jonas Brandt", "Writer")], "Tomas Lind");

        Assert.Equal(["Jonas Brandt", "| CAST", "Tomas Lind"], Reads(movie));
    }

    [Fact]
    public void ALeadWithoutNames_LeavesItsWritersFirst_Unlabelled()
    {
        var movie = Item(CatalogItemTypes.Movie,
            [Crew(" ", "Director"), new CrewCredit(null!, "Director", null, null, null), Crew("Jonas Brandt", "Writer")],
            "Tomas Lind");

        Assert.Equal(["Jonas Brandt", "| CAST", "Tomas Lind"], Reads(movie));
        Assert.Equal([null, "Cast"], DetailCredits.Groups(movie, Server).Select(g => g.Label));
    }

    [Theory]
    [InlineData(CatalogItemTypes.Movie, 12)]
    [InlineData(CatalogItemTypes.Series, 12)]
    [InlineData(CatalogItemTypes.Episode, 20)]
    [InlineData(CatalogItemTypes.Season, 20)]
    public void TheCast_IsTwelveForAMovieOrSeries_TwentyOtherwise_InBillingOrder(string type, int shown)
    {
        // Listed last billed first: the cards still start with the top of the bill.
        var item = Item(type, [Crew("Elena Moreau", "Director")]) with
        {
            Cast = [.. Enumerable.Range(1, 25).Reverse().Select(i => new CastCredit($"Actor {i}", $"Role {i}", i, $"cast-{i}", null, null))],
        };

        var cast = DetailCredits.Groups(item, Server)[^1].People;

        Assert.Equal(shown, cast.Count);
        Assert.Equal(("Actor 1", $"Actor {shown}"), (cast[0].Name, cast[^1].Name));
    }

    [Fact]
    public void ASeries_LeadsWithEveryCreator_AndLeavesOutItsDirectors()
    {
        var series = Item(CatalogItemTypes.Series,
            [Crew("Elena Moreau", "Creator"), Crew("Noor Haddad", "Director"), Crew("Pavel Duric", "Creator"),
             Crew("Sanna Holm", "Creator"), Crew("Jonas Brandt", "Writer")],
            "Tomas Lind");

        Assert.Equal(["Elena Moreau", "Pavel Duric", "Sanna Holm", "| WRITERS", "Jonas Brandt", "| CAST", "Tomas Lind"], Reads(series));
        Assert.All(DetailCredits.Groups(series, Server)[0].People, p => Assert.Equal("Creator", p.Role));
    }

    [Fact]
    public void CastOnly_HasNoLabels()
    {
        var movie = Item(CatalogItemTypes.Movie, [Crew("Leo Marchetti", "Producer")], "Tomas Lind", "Ines Varga");

        var group = Assert.Single(DetailCredits.Groups(movie, Server));

        Assert.Null(group.Label);
        Assert.Equal(["Tomas Lind", "Ines Varga"], Reads(movie));
    }

    [Fact]
    public void Writers_AreAtMostThree_EachOnce_AndNotTheDirectorAgain()
    {
        var movie = Item(CatalogItemTypes.Movie,
            [Crew("Elena Moreau", "Director"), Crew("Elena Moreau", "Writer"), Crew("Jonas Brandt", "Writer"), Crew("Jonas Brandt", "Screenplay"),
             Crew("Mira Okafor", "Writer"), Crew("Pavel Duric", "Writer"), Crew("Sanna Holm", "Writer")]);

        Assert.Equal(["Elena Moreau", "| WRITERS", "Jonas Brandt", "Mira Okafor", "Pavel Duric"], Reads(movie));
    }

    [Fact]
    public void ADirectorPastTheTwoCards_StillShowsAsAWriter()
    {
        var movie = Item(CatalogItemTypes.Movie,
            [Crew("Elena Moreau", "Director"), Crew("Noor Haddad", "Director"), Crew("Pavel Duric", "Director"), Crew("Pavel Duric", "Writer")]);

        Assert.Equal(["Elena Moreau", "Noor Haddad", "| WRITERS", "Pavel Duric"], Reads(movie));
    }

    [Fact]
    public void AnActorWithTwoCharacters_HasACardForEach()
    {
        var movie = Item(CatalogItemTypes.Movie, []) with
        {
            Cast = [new CastCredit("Elena Moreau", "Anna", 0, "7", null, null), new CastCredit("Elena Moreau", "Clara", 1, "7", null, null)],
        };

        Assert.Equal(["Anna", "Clara"], DetailCredits.Groups(movie, Server).Single().People.Select(p => p.Role));
    }

    [Fact]
    public void ASeason_ShowsItsCastAlone()
    {
        var season = Item(CatalogItemTypes.Season, [Crew("Elena Moreau", "Director"), Crew("Jonas Brandt", "Writer")], "Tomas Lind");

        Assert.Equal(["Tomas Lind"], Reads(season));
    }

    [Fact]
    public void NoCredits_IsAnEmptyRow()
    {
        var movie = Item(CatalogItemTypes.Movie, []);

        Assert.Empty(DetailCredits.Groups(movie, Server));
        Assert.Empty(Reads(movie));
    }

    [Fact]
    public void ACard_IsReadWithItsRole()
    {
        Assert.Equal("Elena Moreau, Director", new DetailPerson("Elena Moreau", "Director", null, "1").AccessibleName);
        Assert.Equal("Elena Moreau", new DetailPerson("Elena Moreau", null, null, "1").AccessibleName);
    }
}
