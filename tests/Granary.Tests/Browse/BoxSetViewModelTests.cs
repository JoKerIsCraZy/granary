using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A box set's own page: the web's <c>BoxSetPage</c> look and texts, the apps' Play and refresh.</summary>
public sealed class BoxSetViewModelTests
{
    private const string BoxSetPath = "api/v2/catalog/box-sets/1241";

    private static readonly BoxSetRouteParameter Route = new("1241", "3", "Masterplan Box Set");

    private readonly ScriptedSilo _silo = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };

    public BoxSetViewModelTests()
    {
        _silo.On(HttpMethod.Get, "api/v2/images/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities));
    }

    private BoxSetViewModel Create(IUiDispatcher? dispatcher = null) => new(_session, new BoxSetsApi(_silo.Http),
        new ImageParameterCache(new ImagesApi(_silo.Http), _logs.For<ImageParameterCache>()), _navigation, dispatcher ?? new ImmediateDispatcher(), _time,
        _logs.For<BoxSetViewModel>(), _changes);

    private int Reads => _silo.SentTo(HttpMethod.Get, BoxSetPath).Count;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>A UI thread whose posted work waits until the test runs it.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _work = new();

        public bool HasThreadAccess => true;

        public void Post(Action action) => _work.Enqueue(action);

        public void RunAll()
        {
            while (_work.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private void Answer(params Func<Task<HttpResponseMessage>>[] responders) => _silo.On(HttpMethod.Get, BoxSetPath, responders);

    private static Func<Task<HttpResponseMessage>> Ok(string json) => ScriptedSilo.Json(HttpStatusCode.OK, json);

    private async Task<BoxSetViewModel> LoadAsync()
    {
        var page = Create();
        await page.LoadCommand.ExecuteAsync(Route);
        return page;
    }

    [Fact]
    public async Task ABoxSet_ShowsItsHeroAndFilmsAsTheWeb()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 1)));

        var page = await LoadAsync();

        Assert.True(page.IsReady);
        Assert.Equal("BOX SET", BoxSetViewModel.Overline);
        Assert.Equal("Masterplan Box Set", page.Title);
        Assert.Equal("Three jobs, one crew.", page.Overview);
        Assert.Equal([new DetailChip("3 FILMS"), new DetailChip("2019 – 2028"), new DetailChip("1 OF 3 WATCHED")], page.Facts);
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/box-sets/1241/poster/w342.webp?exp=1&sig=bp"), page.PosterUrl);
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/box-sets/1241/backdrop/w1280.webp?exp=1&sig=bb"), page.BackdropUrl);
        Assert.Equal("3OcRJYB4d3h/iIeHeEh3eIhw+j3A", page.BackdropThumbhash);
        Assert.Equal(["Groundwork", "Masterplan", "Endgame"], page.Films.Select(c => c.Title));
        Assert.Equal("Groundwork, 2019", page.Films[0].AccessibleName);
        // Read with the page's image sizes, in its library.
        var sent = Assert.Single(_silo.SentTo(HttpMethod.Get, BoxSetPath));
        Assert.Contains("library_id=3", sent.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("poster_width=", sent.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileItLoads_TheRoutesTitleNamesThePage()
    {
        var (held, gate) = ItemDetailFixture.Held();
        Answer(held);
        var page = Create();

        var load = page.LoadCommand.ExecuteAsync(Route);

        Assert.True(page.IsLoading);
        Assert.Equal("Masterplan Box Set", page.Title);
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BoxSetPayloads.BoxSet(), System.Text.Encoding.UTF8, "application/json") });
        await load;
        Assert.True(page.IsReady);
    }

    [Fact]
    public async Task NothingPlayed_PlaysTheFirstFilm()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 0)));

        var page = await LoadAsync();

        Assert.Equal("Play Groundwork", page.PlayLabel);
        Assert.Equal([new DetailChip("3 FILMS"), new DetailChip("2019 – 2028")], page.Facts);
        page.PlayCommand.Execute(null);
        var (route, parameter) = Assert.Single(_navigation.Navigations);
        Assert.Equal(AppRoute.Player, route);
        var request = Assert.IsType<PlayRequest>(parameter);
        Assert.Equal(BoxSetPayloads.FirstId, request.ContentId);
        Assert.False(request.FromBeginning);
    }

    [Fact]
    public async Task SomePlayed_ContinuesWithTheFirstFilmNotPlayed()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 2)));

        var page = await LoadAsync();

        Assert.Equal("Continue · Endgame", page.PlayLabel);
        page.PlayCommand.Execute(null);
        Assert.Equal(BoxSetPayloads.LastId, Assert.IsType<PlayRequest>(Assert.Single(_navigation.Navigations).Parameter).ContentId);
    }

    [Fact]
    public async Task AFirstFilmStartedButNotPlayed_Continues()
    {
        // Android's playLabel: a resume point counts as started, though no film is played yet.
        Answer(Ok(BoxSetPayloads.BoxSet(played: 0, nextPosition: 1500)));

        var page = await LoadAsync();

        Assert.Equal("Continue · Groundwork", page.PlayLabel);
    }

    [Fact]
    public async Task AllPlayed_PlaysTheFirstFilmAgain()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 3)));

        var page = await LoadAsync();

        Assert.Equal("Play Groundwork", page.PlayLabel);
        Assert.Contains(new DetailChip("3 OF 3 WATCHED"), page.Facts);
    }

    [Fact]
    public async Task AFilmsCard_OpensItsPage()
    {
        Answer(Ok(BoxSetPayloads.BoxSet()));
        var page = await LoadAsync();

        page.OpenCardCommand.Execute(page.Films[1]);

        Assert.Equal((AppRoute.Item, (object?)ItemDetailPayloads.MovieId), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public async Task NotFound_SaysTheBoxSetIsNotAvailable_InTheWebsWords()
    {
        Answer(ScriptedSilo.Problem(404, "not_found"));

        var page = await LoadAsync();

        Assert.True(page.IsUnavailable);
        Assert.False(page.HasError);
        Assert.Equal("This box set isn't available", BoxSetViewModel.UnavailableTitle);
        Assert.Equal("Box sets may be turned off for this library, or too few of its movies are here.", BoxSetViewModel.UnavailableText);
        Assert.False(page.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedRead_OffersTryAgain()
    {
        Answer(ScriptedSilo.Problem(500, "internal_error"), Ok(BoxSetPayloads.BoxSet()));

        var page = await LoadAsync();

        Assert.True(page.HasError);
        Assert.Equal("Couldn't load this box set", BoxSetViewModel.FailedTitle);
        Assert.Equal("Something went wrong while loading it. Try again in a moment.", BoxSetViewModel.FailedText);

        await page.RetryCommand.ExecuteAsync(null);

        Assert.True(page.IsReady);
        Assert.Equal(3, page.Films.Count);
    }

    [Fact]
    public async Task AChangeOfTheViewersState_ReadsTheBoxSetAgain_SoPlayFollows()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 1)), Ok(BoxSetPayloads.BoxSet(played: 2)));
        var page = await LoadAsync();
        page.Activate();

        _changes.Report();
        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay);
        await page.Refreshing;

        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, BoxSetPath).Count);
        Assert.Equal("Continue · Endgame", page.PlayLabel);
        Assert.Contains(new DetailChip("2 OF 3 WATCHED"), page.Facts);
    }

    [Fact]
    public async Task AFailedRereading_KeepsThePage()
    {
        Answer(Ok(BoxSetPayloads.BoxSet(played: 1)), ScriptedSilo.Problem(503, "dependency_unavailable"));
        var page = await LoadAsync();
        page.Activate();

        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay);
        await page.Refreshing;

        Assert.True(page.IsReady);
        Assert.Equal("Continue · Masterplan", page.PlayLabel);
    }

    [Fact]
    public async Task ABoxSetGoneWhileItShows_SaysItIsNotAvailable()
    {
        // As Android's refresh: box sets turned off, or too few films left to see.
        Answer(Ok(BoxSetPayloads.BoxSet()), ScriptedSilo.Problem(404, "not_found"));
        var page = await LoadAsync();
        page.Activate();

        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay);
        await page.Refreshing;

        Assert.True(page.IsUnavailable);
        Assert.False(page.PlayCommand.CanExecute(null));
        Assert.Empty(page.Films);
    }

    [Fact]
    public async Task LeavingThePage_CancelsAReadingOnItsWay()
    {
        Answer(Ok(BoxSetPayloads.BoxSet()));
        var page = await LoadAsync();
        page.Activate();
        CancellationToken reading = default;
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.OnCancellable(HttpMethod.Get, BoxSetPath, token =>
        {
            reading = token;
            return held.Task.WaitAsync(token);
        });
        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay);

        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, BoxSetPath).Count);
        Assert.False(reading.IsCancellationRequested);

        page.Deactivate();

        Assert.True(reading.IsCancellationRequested);
        await page.Refreshing;
        Assert.True(page.IsReady);
    }

    [Fact]
    public async Task AnOlderLoadAnsweringLate_IsNeverShown()
    {
        var (held, gate) = ItemDetailFixture.Held();
        Answer(held, Ok(BoxSetPayloads.BoxSet(played: 2)));
        var page = Create();
        var older = page.LoadCommand.ExecuteAsync(Route);

        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal("Continue · Endgame", page.PlayLabel);
        gate.SetResult(Json(BoxSetPayloads.BoxSet(played: 0)));
        await older;

        Assert.True(page.IsReady);
        Assert.Equal("Continue · Endgame", page.PlayLabel);
        Assert.Contains(new DetailChip("2 OF 3 WATCHED"), page.Facts);
    }

    [Fact]
    public async Task TwoQuickChanges_ReadOnce_400MillisecondsAfterTheLast()
    {
        Answer(Ok(BoxSetPayloads.BoxSet()));
        var page = await LoadAsync();
        page.Activate();

        _changes.Report();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Reads);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await page.Refreshing;

        Assert.Equal(2, Reads);
        _time.Advance(BoxSetViewModel.RefreshDelay);
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task ATimerThatFiresAfterLeaving_ReadsNothing()
    {
        Answer(Ok(BoxSetPayloads.BoxSet()));
        var dispatcher = new QueuedDispatcher();
        var page = Create(dispatcher);
        await page.LoadCommand.ExecuteAsync(Route);
        page.Activate();
        _changes.Report();
        dispatcher.RunAll();

        // The timer fires and posts its read, which the UI thread reaches only once the page has left.
        _time.Advance(BoxSetViewModel.RefreshDelay);
        page.Deactivate();
        dispatcher.RunAll();
        await page.Refreshing;

        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task AwayFromThePage_ChangesReadNothing()
    {
        Answer(Ok(BoxSetPayloads.BoxSet()));
        var page = await LoadAsync();
        page.Activate();
        page.Deactivate();

        _changes.Report();
        _time.Advance(BoxSetViewModel.RefreshDelay);

        Assert.Single(_silo.SentTo(HttpMethod.Get, BoxSetPath));
    }
}
