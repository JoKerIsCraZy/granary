using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

public sealed class HomeHeroTests
{
    private const string SectionsPath = "api/v2/home/sections";
    private static readonly Uri Server = new("https://silo.example/");

    private readonly BrowseFakeSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();

    private HomeViewModel Create() =>
        new(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(), _time,
            _silo.Logs.For<HomeViewModel>(), new ViewerStateChanges(new PlaybackStops()));

    private async Task<HomeViewModel> HomeWithAsync(params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        var home = Create();
        await home.ActivateAsync();
        return home;
    }

    private static CatalogItem Movie() => new()
    {
        ContentId = "dk",
        Type = CatalogItemTypes.Movie,
        Title = "The Dark Knight",
        Year = 2008,
        Runtime = 152,
        ContentRating = "PG-13",
        Overview = "  Batman raises the stakes in his war on crime.  ",
        BackdropUrl = "/api/v2/artwork/dk/backdrop.webp?sig=b",
        BackdropThumbhash = "hash",
        LogoUrl = "/api/v2/artwork/dk/logo.png?sig=l",
    };

    [Fact]
    public void AMovie_ReadsYearRuntimeAndRating()
    {
        var title = BrowseHeroTitle.From(Movie(), Server);

        Assert.Equal("The Dark Knight", title.Heading);
        Assert.Equal("2008 · 2h 32m · PG-13", title.Meta);
        Assert.Equal("Batman raises the stakes in his war on crime.", title.Overview);
        Assert.Equal(new Uri("https://silo.example/api/v2/artwork/dk/backdrop.webp?sig=b"), title.BackdropUrl);
        Assert.Equal("hash", title.BackdropThumbhash);
        Assert.Equal(new Uri("https://silo.example/api/v2/artwork/dk/logo.png?sig=l"), title.LogoUrl);
    }

    [Fact]
    public void AnEpisode_IsHeadedByItsSeries_AndReadsItsCodeTitleRuntimeAndRating()
    {
        var episode = new CatalogItem
        {
            ContentId = "e1",
            Type = CatalogItemTypes.Episode,
            Title = "Kein Zufall",
            SeriesTitle = "My Name Is Elena",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Year = 2005,
            Runtime = 25,
            ContentRating = "TV-14",
        };

        var title = BrowseHeroTitle.From(episode, Server);

        Assert.Equal("My Name Is Elena", title.Heading);
        Assert.Equal("S1 E1 · Kein Zufall · 25m · TV-14", title.Meta);
    }

    [Fact]
    public void ASeries_ReadsItsYearAndRating()
    {
        var series = new CatalogItem { ContentId = "s", Type = CatalogItemTypes.Series, Title = "Lupin", Year = 2021, Runtime = 45, ContentRating = "TV-MA" };

        Assert.Equal("2021 · TV-MA", BrowseHeroTitle.From(series, Server).Meta);
    }

    [Fact]
    public void WhatATitleLacks_IsLeftOut()
    {
        var bare = new CatalogItem { ContentId = "x", Type = CatalogItemTypes.Movie, Title = "Maya", Overview = "   " };

        var title = BrowseHeroTitle.From(bare, Server);

        Assert.Equal("", title.Meta);
        Assert.Null(title.Overview);
        Assert.Null(title.BackdropUrl);
        Assert.Null(title.LogoUrl);
    }

    [Fact]
    public async Task HomeShows_TheFirstCardOfTheFirstRow()
    {
        var home = await HomeWithAsync(Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("recent", "recently_added", 2, Item("r1"), Item("r2")));

        Assert.Equal("m1", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task TheFirstCardPointedAt_ShowsAtOnce_LaterOnesOnceThePointerRests()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 3, Item("r1"), Item("r2"), Item("r3")));
        var cards = home.Rows[0].Cards;

        home.PointCardCommand.Execute(cards[1]);
        Assert.Equal("r2", home.Hero.Shown?.Key);

        home.PointCardCommand.Execute(cards[2]);
        _time.Advance(BrowseHeroViewModel.SettleTime - TimeSpan.FromMilliseconds(1));
        Assert.Equal("r2", home.Hero.Shown?.Key);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("r3", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task PassingOverManyCards_ShowsOnlyTheOneThePointerStopsAt()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 4, Item("r1"), Item("r2"), Item("r3"), Item("r4")));
        var cards = home.Rows[0].Cards;
        home.PointCardCommand.Execute(cards[0]);
        var shown = new List<string?>();
        home.Hero.PropertyChanged += (_, e) => shown.Add(home.Hero.Shown?.Key);

        foreach (var card in cards.Skip(1))
        {
            home.PointCardCommand.Execute(card);
            _time.Advance(TimeSpan.FromMilliseconds(60));
        }
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal(["r4"], shown);
    }

    [Fact]
    public async Task ComingBackToTheShownCard_KeepsIt()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 2, Item("r1"), Item("r2")));
        var cards = home.Rows[0].Cards;
        home.PointCardCommand.Execute(cards[0]);

        home.PointCardCommand.Execute(cards[1]);
        home.PointCardCommand.Execute(cards[0]);
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal("r1", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task AReload_KeepsTheShownTitle_OrFallsBackToTheFirstWhenItIsGone()
    {
        var changes = new ViewerStateChanges(new PlaybackStops());
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 2, Item("r1"), Item("r2"))));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(),
            _time, _silo.Logs.For<HomeViewModel>(), changes);
        await home.ActivateAsync();
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);

        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 3, Item("r0"), Item("r1"), Item("r2"))));
        changes.Report();
        await home.Loading;
        Assert.Equal("r2", home.Hero.Shown?.Key);

        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 1, Item("r9"))));
        changes.Report();
        await home.Loading;
        Assert.Equal("r9", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task APointWaitingToSettle_DoesNotOutliveAReload()
    {
        var changes = new ViewerStateChanges(new PlaybackStops());
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 2, Item("r1"), Item("r2"))));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(),
            _time, _silo.Logs.For<HomeViewModel>(), changes);
        await home.ActivateAsync();
        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);

        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 1, Item("r1"))));
        changes.Report();
        await home.Loading;
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal("r1", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task APointResting_SurvivesAReloadThatKeepsItsCard()
    {
        var changes = new ViewerStateChanges(new PlaybackStops());
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 2, Item("r1"), Item("r2"))));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(),
            _time, _silo.Logs.For<HomeViewModel>(), changes);
        await home.ActivateAsync();
        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);

        changes.Report();
        await home.Loading;
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal("r2", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task RemovingTheShownTitle_ShowsTheFirstCardLeft()
    {
        _silo.Map("api/v2/home/dismissals/continue_watching/m1", _ => BrowseFakeSilo.NoContent());
        var home = await HomeWithAsync(Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("recent", "recently_added", 1, Item("r1")));
        Assert.Equal("m1", home.Hero.Shown?.Key);

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        Assert.Equal("r1", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task AnotherProfile_StartsAfresh_ItsFirstPointShowingAtOnce()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 2, Item("r1"), Item("r2")));
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 2, Item("k1"), Item("k2"))));

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;
        Assert.Equal("k1", home.Hero.Shown?.Key);

        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        Assert.Equal("k2", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task AProfileWithoutRows_HidesTheHero()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 1, Item("r1")));
        _silo.Map(SectionsPath, Sections(Section("nothing", "genre", 0)));

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;

        Assert.Null(home.Hero.Shown);
    }

    [Fact]
    public void AnEpisodeWithoutItsNumbers_LeavesTheCodeOut()
    {
        var episode = new CatalogItem
        {
            ContentId = "e1", Type = CatalogItemTypes.Episode, Title = "Kein Zufall", SeriesTitle = "Elena", SeasonNumber = 1, Runtime = 25,
            ContentRating = "TV-14",
        };

        Assert.Equal("Kein Zufall · 25m · TV-14", BrowseHeroTitle.From(episode, Server).Meta);
    }

    [Fact]
    public async Task AnEmptyHome_ShowsNoTitle()
    {
        var home = await HomeWithAsync(Section("nothing", "genre", 0));

        Assert.Null(home.Hero.Shown);
    }

    [Fact]
    public async Task ACardFromNoRow_IsIgnored()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 1, Item("r1")));

        home.PointCardCommand.Execute(new MediaCard("elsewhere", "movie", "Elsewhere"));
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal("r1", home.Hero.Shown?.Key);
    }
}
