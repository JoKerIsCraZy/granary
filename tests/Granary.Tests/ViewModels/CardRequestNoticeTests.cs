using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>A card's failed Request said on its page, as the web's toast: for a while, and not hidden by another card's success.</summary>
public sealed class CardRequestNoticeTests
{
    private const string CreatePath = "api/v2/requests";

    private const string Created = """
        { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "active", "state": "pending",
          "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-29T12:00:00Z", "updated_at": "2026-10-29T12:00:00Z" }
        """;

    private static readonly MediaCard Matrix = Card(603, "The Matrix");
    private static readonly MediaCard Dune = Card(438631, "Dune");

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly ManualTimeProvider _clock = new();

    private static MediaCard Card(long tmdbId, string title) => RequestCards.Of(new RequestMediaResult
    {
        TmdbId = tmdbId,
        MediaType = "movie",
        Title = title,
        Request = new RequestMediaState { Requestable = true },
    });

    private async Task<(MediaCardActions Actions, CardRequestNotice Notice)> CreateAsync()
    {
        _silo.On(HttpMethod.Get, "api/v2/requests/status", ScriptedSilo.Json(HttpStatusCode.OK, """{ "requests_enabled": true }"""));
        var requests = new RequestsApi(_silo.Http);
        var feature = new RequestsFeature(_session, requests, _clock, _logs.For<RequestsFeature>());
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        var actions = new MediaCardActions(new CatalogApi(_silo.Http), _session, new RecordingNavigation(), _logs.For<MediaCardActions>(),
            new ViewerStateChanges(new PlaybackStops()), requests, feature);
        var notice = new CardRequestNotice(actions, _session, _clock, new ImmediateDispatcher());
        notice.Attach();
        return (actions, notice);
    }

    private void Fails() => _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(502, "bad_gateway"));

    private void Succeeds() => _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Created));

    [Fact]
    public async Task AFailure_ShowsForAWhile_AsTheWebsToast()
    {
        var (actions, notice) = await CreateAsync();
        Fails();

        await actions.RequestAsync(Matrix);
        Assert.Equal("Failed to submit request", notice.Text);
        Assert.True(notice.HasText);

        _clock.Advance(CardRequestNotice.ShowTime - TimeSpan.FromMilliseconds(1));
        Assert.True(notice.HasText);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task AnotherCardsSuccess_DoesNotHideAFailure()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);
        Succeeds();

        Assert.True(await actions.RequestAsync(Dune));

        Assert.Equal("Failed to submit request", notice.Text);
    }

    [Fact]
    public async Task TheSameCardsSuccess_ClearsItsFailure()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);
        Succeeds();

        Assert.True(await actions.RequestAsync(Matrix));

        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task ALaterFailure_ShowsItsFullTime()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);
        _clock.Advance(CardRequestNotice.ShowTime / 2);

        await actions.RequestAsync(Dune);
        _clock.Advance(CardRequestNotice.ShowTime / 2);

        Assert.True(notice.HasText);
        _clock.Advance(CardRequestNotice.ShowTime / 2);
        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task AReload_ClearsIt()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);

        notice.Clear();

        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task AnotherProfile_ClearsIt()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task APageThatLeft_SaysNothing()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);

        notice.Detach();
        Assert.False(notice.HasText);
        await actions.RequestAsync(Dune);

        Assert.False(notice.HasText);
    }

    [Fact]
    public async Task APageThatCameBack_SaysFailuresAgain()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        notice.Detach();

        notice.Attach();
        await actions.RequestAsync(Matrix);

        Assert.Equal("Failed to submit request", notice.Text);
    }

    [Fact]
    public async Task TheSameFailureAgain_IsSaidAgain()
    {
        var (actions, notice) = await CreateAsync();
        Fails();
        await actions.RequestAsync(Matrix);
        var said = new List<string>();
        notice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardRequestNotice.Text))
            {
                said.Add(notice.Text);
            }
        };

        await actions.RequestAsync(Matrix);

        Assert.Equal(["", "Failed to submit request"], said);
    }

    [Fact]
    public void APageWithoutCardRequests_HasOneThatSaysNothing()
    {
        var none = CardRequestNotice.None;

        none.Attach();
        none.Detach();

        Assert.False(none.HasText);
    }
}
