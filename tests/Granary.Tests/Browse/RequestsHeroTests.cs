using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary>The hero at the top of Requests' Discover, fed by its rows of titles from TMDB.</summary>
public sealed class RequestsHeroTests
{
    private const string Discover = """
        {
          "items": [
            {
              "key": "trending", "title": "Trending", "page": 1, "total_pages": 1, "total_results": 4,
              "results": [
                { "tmdb_id": 10, "media_type": "movie", "title": "New One", "year": 2026, "overview": "  A new one.  ", "poster_path": "/new.jpg",
                  "backdrop_path": "/new-wide.jpg", "availability": "none", "request": { "requestable": true } },
                { "tmdb_id": 11, "media_type": "series", "title": "Asked For", "year": 2025, "availability": "none",
                  "request": { "requestable": false, "reason": "already_requested", "status": "approved", "state": "approved" } },
                { "tmdb_id": 12, "media_type": "movie", "title": "Got It", "year": 2020, "availability": "available", "library_content_id": "movie:got-it-2020",
                  "request": { "requestable": false, "reason": "already_available" } },
                { "tmdb_id": 13, "media_type": "movie", "title": "Not For You", "availability": "none", "request": { "requestable": false, "reason": "blocked" } }
              ]
            }
          ]
        }
        """;

    private readonly LibraryHarness _harness = new();

    public RequestsHeroTests()
    {
        _harness.Answer("api/v2/requests/mine", """{ "items": [] }""");
        _harness.Answer("api/v2/requests/discover", Discover);
        foreach (string brands in new[] { "studios", "networks", "genres" })
        {
            _harness.Answer($"api/v2/requests/discover/{brands}", """{ "items": [] }""");
        }
    }

    private async Task<RequestsViewModel> LoadedAsync()
    {
        var hub = new RequestsViewModel(_harness.Session, new RequestsApi(_harness.Silo.Http), _harness.Navigation, _harness.Clock,
            new ImmediateDispatcher(), _harness.Logs.For<RequestsViewModel>());
        await hub.ActivateAsync(isReturn: false);
        return hub;
    }

    private static void Point(RequestsViewModel hub, string title) =>
        hub.DiscoverRows[0].PointCardCommand!.Execute(hub.DiscoverRows[0].Cards.Single(c => c.Title == title));

    [Fact]
    public async Task TheHero_ShowsTheFirstTitle_WithItsBackdropAndOverview_AndItsNameForALogo()
    {
        var hub = await LoadedAsync();

        var shown = Assert.IsType<BrowseHeroTitle>(hub.Hero.Shown);
        Assert.Equal(("tmdb:movie:10", "New One", "2026 · Movie", "A new one."), (shown.Key, shown.Heading, shown.Meta, shown.Overview));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w1280/new-wide.jpg"), shown.BackdropUrl);
        Assert.Null(shown.LogoUrl);
    }

    [Theory]
    [InlineData("Asked For", "2025 · Series · Approved")]
    [InlineData("Got It", "2020 · Movie · Available")]
    [InlineData("Not For You", "Movie · Blocked")]
    public async Task TheMetaLine_SaysYearTypeAndWhatTheRequestStandsAt(string title, string meta)
    {
        var hub = await LoadedAsync();

        Point(hub, title);

        Assert.Equal((title, meta), (hub.Hero.Shown?.Heading, hub.Hero.Shown?.Meta));
    }

    [Fact]
    public async Task ACardPointedAt_LeadsTheHero_TheFirstAtOnce_ThenOnceItSettles()
    {
        var hub = await LoadedAsync();

        Point(hub, "Got It");
        Assert.Equal("Got It", hub.Hero.Shown?.Heading);

        Point(hub, "Asked For");
        Assert.Equal("Got It", hub.Hero.Shown?.Heading);
        _harness.Clock.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal("Asked For", hub.Hero.Shown?.Heading);
    }

    [Fact]
    public async Task NothingToDiscover_HidesTheHero()
    {
        _harness.Answer("api/v2/requests/discover", """{ "items": [] }""");

        var hub = await LoadedAsync();

        Assert.Null(hub.Hero.Shown);
    }
}
