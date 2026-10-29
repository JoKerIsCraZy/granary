using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>What the Requests hub does when a Discover card's Request answers, as the web's toast and its refetch of the requests.</summary>
public sealed class RequestsCardRequestTests
{
    private const string MinePath = "api/v2/requests/mine";
    private const string CreatePath = "api/v2/requests";

    private const string Discover = """
        {
          "items": [
            {
              "key": "trending_movies", "title": "Trending Movies", "page": 1, "total_pages": 1, "total_results": 1,
              "results": [
                { "tmdb_id": 10, "media_type": "movie", "title": "New One", "year": 2026, "poster_path": "/new.jpg", "availability": "none",
                  "in_watchlist": false, "request": { "requestable": true, "following": false, "requested_by_viewer": false } }
              ]
            }
          ]
        }
        """;

    private const string NoRequests = """{ "items": [], "page": { "has_more": false } }""";

    private const string OneRequest = """
        { "items": [ { "id": "r9", "media_type": "movie", "tmdb_id": 10, "title": "New One", "status": "pending", "outcome": "active", "state": "pending",
          "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-29T12:00:00Z", "updated_at": "2026-10-29T12:00:00Z" } ],
          "page": { "has_more": false } }
        """;

    private const string Created = """
        { "id": "r9", "media_type": "movie", "tmdb_id": 10, "title": "New One", "status": "pending", "outcome": "active", "state": "pending",
          "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-29T12:00:00Z", "updated_at": "2026-10-29T12:00:00Z" }
        """;

    private readonly LibraryHarness _harness = new();
    private MediaCardActions? _actions;

    public RequestsCardRequestTests()
    {
        _harness.Answer(MinePath, NoRequests);
        _harness.Answer("api/v2/requests/discover", Discover);
        _harness.Answer("api/v2/requests/discover/studios", """{ "items": [] }""");
        _harness.Answer("api/v2/requests/discover/networks", """{ "items": [] }""");
        _harness.Answer("api/v2/requests/discover/genres", """{ "items": [] }""");
        _harness.Answer("api/v2/requests/status", """{ "requests_enabled": true, "watchlist_titles_supported": true }""");
    }

    private async Task<RequestsViewModel> LoadedAsync()
    {
        var requests = new RequestsApi(_harness.Silo.Http);
        var feature = new RequestsFeature(_harness.Session, requests, _harness.Clock, _harness.Logs.For<RequestsFeature>());
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        _actions = new MediaCardActions(new CatalogApi(_harness.Silo.Http), _harness.Session, _harness.Navigation, _harness.Logs.For<MediaCardActions>(),
            new ViewerStateChanges(new PlaybackStops()), requests, feature);
        var hub = new RequestsViewModel(_harness.Session, requests, _harness.Navigation, _harness.Clock, new ImmediateDispatcher(),
            _harness.Logs.For<RequestsViewModel>(), _actions, new CardRequestNotice(_actions, _harness.Session, _harness.Clock, new ImmediateDispatcher()));
        await hub.ActivateAsync(isReturn: false);
        return hub;
    }

    private static MediaCard Card(RequestsViewModel hub) => Assert.Single(Assert.Single(hub.DiscoverRows).Cards);

    [Fact]
    public async Task ARequestFromACard_ReadsYoursAgain()
    {
        _harness.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Created));
        var hub = await LoadedAsync();
        Assert.Equal("", hub.YoursCount);
        _harness.Answer(MinePath, OneRequest);

        Assert.True(await _actions!.RequestAsync(Card(hub)));
        await WaitUntilAsync(() => hub.YoursCount == "1");

        Assert.Equal("", hub.RequestNotice.Text);
    }

    [Fact]
    public async Task AFailedRequestFromACard_SaysSoOnDiscover()
    {
        _harness.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(502, "bad_gateway"));
        var hub = await LoadedAsync();

        Assert.False(await _actions!.RequestAsync(Card(hub)));

        Assert.Equal("Failed to submit request", hub.RequestNotice.Text);
        Assert.True(hub.RequestNotice.HasText);
    }

    [Fact]
    public async Task TheFailure_GoesWithTheNextTry()
    {
        _harness.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(502, "bad_gateway"));
        var hub = await LoadedAsync();
        await _actions!.RequestAsync(Card(hub));
        _harness.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Created));

        Assert.True(await _actions.RequestAsync(Card(hub)));

        Assert.False(hub.RequestNotice.HasText);
    }

    [Fact]
    public async Task AHubThatLeft_HearsNoMoreAnswers()
    {
        _harness.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(502, "bad_gateway"));
        var hub = await LoadedAsync();
        hub.Deactivate();

        await _actions!.RequestAsync(Card(hub));

        Assert.False(hub.RequestNotice.HasText);
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
