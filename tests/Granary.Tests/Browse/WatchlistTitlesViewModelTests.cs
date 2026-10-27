using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Requests;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>
/// The watchlist's "Not in your library yet" tab, as Silo's web shows it (WatchlistTabs, WatchlistTitlesTab); the clock
/// stands on 1 October 2026, 12:00 UTC.
/// </summary>
public sealed class WatchlistTitlesViewModelTests
{
    internal const string StatusPath = "api/v2/requests/status";
    private const string TitlesPath = "api/v2/watchlist/titles";
    private const string Kept = """{ "requests_enabled": true, "watchlist_titles_supported": true, "watchlist_requests": true }""";
    private const string NotRequested = """{ "requestable": true }""";
    private const string Pending = """{ "requestable": false, "status": "pending" }""";
    private const string Approved = """{ "requestable": false, "status": "approved" }""";
    private const string Downloading = """
        { "requestable": false, "status": "downloading",
          "download": { "phase": "downloading", "percent": 10, "downloads": 1, "updated_at": "2026-10-01T12:00:00Z" } }
        """;

    private readonly LibraryHarness _harness = new();

    public WatchlistTitlesViewModelTests()
    {
        _harness.Answer(StatusPath, Kept);
        _harness.Answer(TitlesPath, Page(Title(4)));
    }

    internal static WatchlistTitlesViewModel CreateTitles(LibraryHarness harness, ViewerStateChanges? changes)
    {
        var requests = new RequestsApi(harness.Silo.Http);
        var feature = new RequestsFeature(harness.Session, requests, harness.Clock, harness.Logs.For<RequestsFeature>());
        return new WatchlistTitlesViewModel(harness.Session, requests, feature, harness.Navigation, harness.Clock, new ImmediateDispatcher(),
            harness.Logs.For<WatchlistTitlesViewModel>(), changes);
    }

    internal static PersonalListViewModel CreateList(LibraryHarness harness, ViewerStateChanges changes) => new(
        harness.Session,
        new CatalogApi(harness.Silo.Http),
        new ImageParameterCache(new ImagesApi(harness.Silo.Http), harness.Logs.For<ImageParameterCache>()),
        harness.Navigation,
        CreateTitles(harness, changes),
        harness.Logs.For<PersonalListViewModel>(),
        changes);

    private WatchlistTitlesViewModel Create() => CreateTitles(_harness, _harness.ViewerState);

    private async Task<WatchlistTitlesViewModel> ShownAsync()
    {
        var titles = Create();
        await titles.ActivateAsync();
        return titles;
    }

    /// <summary>"Movie 4", a movie of 2020 saved on <paramref name="addedAt"/>.</summary>
    private static string Title(int id, string request = NotRequested, string status = "active", string? releaseDate = null,
        string addedAt = "2026-09-01T00:00:00Z") =>
        $$"""
        { "tmdb_id": {{id}}, "media_type": "movie", "title": "Movie {{id}}", "year": 2020, "poster_path": "/m{{id}}.jpg",
          {{(releaseDate is null ? "" : $"\"release_date\": \"{releaseDate}\",")}} "status": "{{status}}", "added_at": "{{addedAt}}",
          "request": {{request}} }
        """;

    private static string Page(params string[] titles) => PageBefore(null, titles);

    /// <summary>A page with <paramref name="next"/> as the cursor of the one after it.</summary>
    private static string PageBefore(string? next, params string[] titles) =>
        $$"""
        { "items": [{{string.Join(",", titles)}}],
          "page": {{(next is null ? """{ "has_more": false }""" : $$"""{ "has_more": true, "next_cursor": "{{next}}" }""")}} }
        """;

    private static string[] Ids(WatchlistTitlesViewModel titles) => [.. titles.Cards.Select(c => c.Id)];

    [Fact]
    public async Task Titles_ShowSoonestFirst_WithTheirStatusOnThePosterAndInWords()
    {
        _harness.Answer(TitlesPath, Page(
            Title(5, status: "removed"),
            Title(4),
            Title(3, Pending),
            Title(22, Approved, releaseDate: "2027-03-01"),
            Title(21, Approved, releaseDate: "2026-11-01"),
            Title(1, Downloading)));

        var titles = await ShownAsync();

        Assert.Equal(["tmdb:movie:1", "tmdb:movie:21", "tmdb:movie:22", "tmdb:movie:3", "tmdb:movie:4", "tmdb:movie:5"], Ids(titles));
        Assert.Equal(["DOWNLOADING 10%", "OUT NOV 1", "OUT MAR 1, 2027", "AWAITING APPROVAL", "NOT REQUESTED", "NOT ON TMDB"],
            titles.Cards.Select(c => c.Overlay?.RequestStatus?.ToUpperInvariant()));
        Assert.Equal(["Downloading · 10%", "Out Nov 1 · approved", "Out Mar 1, 2027 · approved", "Awaiting approval", "Not requested",
            "No longer listed on TMDB"], titles.Cards.Select(c => c.Caption));
        Assert.Equal([false, false, false, false, false, true], titles.Cards.Select(c => c.IsCaptionAttention));
        var card = titles.Cards[0];
        Assert.Equal(("Movie 1", "MOVIE · 2020", RequestsViewModel.CardType, true), (card.Title, card.Subtitle, card.Type, card.InWatchlist));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w342/m1.jpg"), card.ImageUrl);
        Assert.Equal(6, titles.Count);
        Assert.True(titles.NeedsAttention);
        Assert.True(titles.IsReady);
        Assert.True(titles.IsAvailable);
    }

    [Fact]
    public async Task TheList_IsReadPageByPage()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Json(HttpStatusCode.OK, PageBefore("c1", Title(1))), ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(2))));

        var titles = await ShownAsync();

        Assert.Equal(["limit=200", "limit=200&cursor=c1"], _harness.Queries(TitlesPath));
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public async Task ACursorTheServerRepeats_FailsTheRead()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Json(HttpStatusCode.OK, PageBefore("c1", Title(1))));

        var titles = await ShownAsync();

        Assert.Equal(2, _harness.Count(TitlesPath));
        Assert.True(titles.HasError);
        Assert.Empty(titles.Cards);
    }

    [Theory]
    [InlineData("""{ "requests_enabled": true, "watchlist_titles_supported": false }""")]
    [InlineData("""{ "requests_enabled": false, "watchlist_titles_supported": true }""")]
    public async Task AServerThatKeepsNoSuchTitles_IsNotAsked(string status)
    {
        _harness.Answer(StatusPath, status);

        var titles = await ShownAsync();

        Assert.False(titles.IsAvailable);
        Assert.Equal(0, _harness.Count(TitlesPath));
    }

    [Theory]
    [InlineData(true, "These titles aren't in the library yet, so they can't be played. They've been requested for you. When one arrives, it moves to In your library and you get a notification.")]
    [InlineData(false, "These titles aren't in the library yet, so they can't be played. When one arrives, it moves to In your library.")]
    public async Task TheHint_SaysWhetherAddingRequested(bool requests, string hint)
    {
        _harness.Answer(StatusPath, $$"""{ "requests_enabled": true, "watchlist_titles_supported": true, "watchlist_requests": {{(requests ? "true" : "false")}} }""");

        var titles = await ShownAsync();

        Assert.Equal(hint, titles.Hint);
    }

    [Fact]
    public async Task NoTitles_SaysSo_AndBrowseDiscoverOpensRequests()
    {
        _harness.Answer(TitlesPath, Page());

        var titles = await ShownAsync();
        titles.BrowseDiscoverCommand.Execute(null);

        Assert.True(titles.IsEmpty);
        Assert.False(titles.HasCount);
        Assert.Equal((AppRoute.Requests, (object?)null), Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task AFailedRead_OffersRetry()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Problem(500, "internal_error"), ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(4))));

        var titles = await ShownAsync();
        Assert.True(titles.HasError);

        await titles.RetryCommand.ExecuteAsync(null);

        Assert.True(titles.IsReady);
        Assert.Equal(["tmdb:movie:4"], Ids(titles));
    }

    [Fact]
    public async Task WhileATitleDownloads_ItIsReadAgainEvery30Seconds_AndAFailedReadKeepsWhatIsShown()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(1, Downloading))),
            ScriptedSilo.Problem(502, "upstream_error"), ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(1, Approved))));
        var titles = await ShownAsync();

        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);
        await titles.Loading;
        Assert.Equal(2, _harness.Count(TitlesPath));
        Assert.True(titles.IsReady);
        Assert.Equal("Downloading · 10%", titles.Cards[0].Caption);

        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);
        await titles.Loading;
        Assert.Equal("Approved · waiting for a download", titles.Cards[0].Caption);

        // Nothing downloads any more: no more reads.
        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval * 3);
        Assert.Equal(3, _harness.Count(TitlesPath));
    }

    [Fact]
    public async Task OffTheWatchlist_NothingIsReadAgain()
    {
        _harness.Answer(TitlesPath, Page(Title(1, Downloading)));
        var titles = await ShownAsync();

        titles.Deactivate();
        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval * 2);
        _harness.ViewerState.Report();

        Assert.Equal(1, _harness.Count(TitlesPath));
    }

    [Fact]
    public async Task AChangeToTheWatchlist_ReadsItAgain()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(4), Title(5))), ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(5))));
        var titles = await ShownAsync();

        // A card took Movie 4 off.
        _harness.ViewerState.Report();
        await titles.Loading;

        Assert.Equal(["tmdb:movie:5"], Ids(titles));
        // Telling the others that Movie 4 left is not a reason to read again.
        Assert.Equal(2, _harness.Count(TitlesPath));
    }

    [Fact]
    public async Task AReadThatAnswersAfterThePageLeft_ShowsNothing()
    {
        var titles = await ShownAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(TitlesPath, LibraryHarness.Held(release, Page(Title(9))));

        var reading = titles.ActivateAsync();
        titles.Deactivate();
        release.SetResult();
        await reading;

        Assert.Equal(["tmdb:movie:4"], Ids(titles));
    }

    [Fact]
    public async Task ASeries_SaysSo()
    {
        _harness.Answer(TitlesPath, Page(Title(4).Replace("\"media_type\": \"movie\"", "\"media_type\": \"series\"", StringComparison.Ordinal)));

        var titles = await ShownAsync();

        Assert.Equal(("tmdb:series:4", "SERIES · 2020"), (titles.Cards[0].Id, titles.Cards[0].Subtitle));
    }

    [Fact]
    public async Task ACard_OpensTheTitlesPage()
    {
        var titles = await ShownAsync();

        titles.OpenCardCommand.Execute(titles.Cards[0]);

        Assert.Equal((AppRoute.Title, (object?)new TitleRouteParameter("movie", 4, "Movie 4")), Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task AnotherProfile_ShowsNoneOfTheLastOnes()
    {
        var titles = await ShownAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(TitlesPath, LibraryHarness.Held(release, Page(Title(9))));
        _harness.Session.ProfileId = "8";

        var reading = titles.ActivateAsync();
        Assert.Empty(titles.Cards);
        Assert.False(titles.IsReady);
        release.SetResult();
        await reading;

        Assert.Equal(["tmdb:movie:9"], Ids(titles));
    }

    [Fact]
    public async Task TheWatchlist_ShowsTwoTabs_WhileTheServerKeepsSuchTitles()
    {
        var list = CreateList(_harness, _harness.ViewerState);

        await list.ActivateAsync("watchlist");
        Assert.True(list.ShowsTabs);
        Assert.True(list.ShowsLibrary);
        Assert.Equal(1, list.Titles.Count);

        list.ShowTitlesTabCommand.Execute(null);
        Assert.True(list.ShowsTitles);
        Assert.False(list.ShowsLibrary);

        await list.ActivateAsync("favorites");
        Assert.False(list.ShowsTabs);
        Assert.False(list.IsTitlesTab);
        Assert.True(list.ShowsLibrary);
    }

    [Fact]
    public async Task OtherLists_DoNotReadTheTitles()
    {
        var list = CreateList(_harness, _harness.ViewerState);

        await list.ActivateAsync("favorites");

        Assert.Equal(0, _harness.Count(TitlesPath));
        Assert.False(list.ShowsTabs);
    }

    [Fact]
    public async Task ATitleThatLeft_ReloadsTheLibrarysWatchlist()
    {
        _harness.Answer(TitlesPath, ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(4), Title(5))), ScriptedSilo.Json(HttpStatusCode.OK, Page(Title(5))));
        var list = CreateList(_harness, _harness.ViewerState);
        await list.ActivateAsync("watchlist");
        int catalogReads = _harness.Count(LibraryHarness.CatalogPath);

        // Movie 4 arrived in the library: the next read no longer lists it.
        await list.ActivateAsync("watchlist");

        await WaitUntilAsync(() => _harness.Count(LibraryHarness.CatalogPath) > catalogReads);
        Assert.Equal(["tmdb:movie:5"], Ids(list.Titles));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
