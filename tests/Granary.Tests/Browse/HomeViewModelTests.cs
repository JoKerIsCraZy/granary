using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

public sealed class HomeViewModelTests
{
    private const string SectionsPath = "api/v2/home/sections";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly TestClock _clock = new(ManualTimeProvider.DefaultStart);
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    private HomeViewModel Create() =>
        new(_session, _silo.Catalog, _silo.Images, _navigation, new ImmediateDispatcher(), _clock, _silo.Logs.For<HomeViewModel>(), _changes);

    [Fact]
    public async Task ViewerStateChangedWhileAway_ReloadsOnTheNextVisit()
    {
        // A movie started from a detail page: Continue Watching has it when Home shows again, not five minutes later.
        int calls = 0;
        _silo.Map(SectionsPath, _ => BrowseFakeSilo.Json(Sections(Section("cw", "continue_watching", 1, Item("m" + Interlocked.Increment(ref calls))))));
        var home = Create();
        await home.ActivateAsync();
        home.Deactivate();

        _changes.Report();

        Assert.Single(_silo.To(SectionsPath));
        await home.ActivateAsync();
        Assert.Equal(2, _silo.To(SectionsPath).Count);
        Assert.Equal("m2", home.Rows[0].Cards[0].Id);

        home.Deactivate();
        await home.ActivateAsync();
        Assert.Equal(2, _silo.To(SectionsPath).Count);
    }

    [Fact]
    public async Task ViewerStateChangedWhileShown_ReloadsAtOnce_KeepingTheRowsInPlace()
    {
        _silo.Map(SectionsPath, Sections(
            Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("recent", "recently_added", 3, Item("r1"), Item("r2"), Item("r3"))));
        var home = Create();
        await home.ActivateAsync();
        var continueWatching = home.Rows[0];
        var recent = home.Rows[1];
        var r2 = recent.Cards[1];
        _silo.Map(SectionsPath, Sections(
            Section("cw", "continue_watching", 2, Item("m2", progressUpdatedAt: "2026-10-25T11:00:00Z"), Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("recent", "recently_added", 3, Item("r1"), Item("r2"), Item("r3"))));

        _changes.Report();
        await home.Loading;

        Assert.Same(continueWatching, home.Rows[0]);
        Assert.Same(recent, home.Rows[1]);
        Assert.Equal(["m2", "m1"], continueWatching.Cards.Select(c => c.Id));
        Assert.Same(r2, recent.Cards[1]);
    }

    [Fact]
    public async Task ViewerStateChanged_WithAnotherSetOfRows_ShowsTheNewRows()
    {
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 1, Item("r1"))));
        var home = Create();
        await home.ActivateAsync();
        _silo.Map(SectionsPath, Sections(
            Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("recent", "recently_added", 1, Item("r1"))));

        _changes.Report();
        await home.Loading;

        Assert.Equal(["cw", "recent"], home.Rows.Select(r => r.SectionId));
    }

    [Fact]
    public async Task RemovingACard_IsAViewerStateChange()
    {
        _silo.Map(SectionsPath, Sections(Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z"))));
        _silo.Map("api/v2/home/dismissals/continue_watching/m1", _ => BrowseFakeSilo.NoContent());
        var home = Create();
        await home.ActivateAsync();

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        // Other pages' rows are stale now; Home took the card off itself and does not load again.
        Assert.Equal(1, _changes.Version);
        Assert.Single(_silo.To(SectionsPath));
        home.Deactivate();
        await home.ActivateAsync();
        Assert.Single(_silo.To(SectionsPath));
    }

    [Fact]
    public async Task Load_HidesRowsWithoutCardsAndRefetchesRowsThatCameBackEmpty()
    {
        _silo.Map(SectionsPath, Sections(
            Section("recent", "recently_added", 2, Item("m1"), Item("m2")),
            Section("nothing", "genre", 0),
            Section("lazy", "genre", 12)));
        _silo.Map(SectionsPath + "/lazy/items", Serialize(Section("lazy", "genre", 12, Item("m3"))));
        var home = Create();

        await home.ActivateAsync();

        Assert.Equal(["recent", "lazy"], home.Rows.Select(r => r.SectionId));
        Assert.Equal(["m3"], home.Rows[1].Cards.Select(c => c.Id));
        Assert.Empty(_silo.To(SectionsPath + "/nothing/items"));
        Assert.False(home.IsLoading);
        Assert.Equal("", home.StatusText);
    }

    [Fact]
    public async Task Refetch_AsksAtMostFourRowsAtOnce()
    {
        string[] ids = ["a", "b", "c", "d", "e", "f"];
        _silo.Map(SectionsPath, Sections([.. ids.Select(id => Section(id, "genre", 3))]));
        int inFlight = 0, maxInFlight = 0, arrived = 0;
        var fourArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (string id in ids)
        {
            _silo.Map($"{SectionsPath}/{id}/items", async (_, _) =>
            {
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                if (Interlocked.Increment(ref arrived) == 4)
                {
                    fourArrived.TrySetResult();
                }
                await open.Task;
                Interlocked.Decrement(ref inFlight);
                return BrowseFakeSilo.Json(Serialize(Section(id, "genre", 3, Item("m-" + id))));
            });
        }
        var home = Create();

        var loading = home.ActivateAsync();
        await fourArrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, Volatile.Read(ref arrived));
        open.SetResult();
        await loading;

        Assert.Equal(4, maxInFlight);
        Assert.Equal(ids, home.Rows.Select(r => r.SectionId));
    }

    [Fact]
    public async Task FailedRefetch_LeavesTheRowOutAndTriesAgainOnTheNextVisit()
    {
        _silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 1, Item("m1")), Section("lazy", "genre", 5)));
        _silo.Map(SectionsPath + "/lazy/items", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var home = Create();

        await home.ActivateAsync();
        Assert.Equal(["recent"], home.Rows.Select(r => r.SectionId));
        Assert.Contains(_silo.Logs.Lines, l => l.StartsWith("Warning HomeViewModel", StringComparison.Ordinal));

        _silo.Map(SectionsPath + "/lazy/items", Serialize(Section("lazy", "genre", 5, Item("m2"))));
        home.Deactivate();
        await home.ActivateAsync();

        Assert.Equal(["recent", "lazy"], home.Rows.Select(r => r.SectionId));
    }

    [Fact]
    public async Task ContinueWatching_KeepsOnlyVideoCardsAsLandscapeCards()
    {
        _silo.Map(SectionsPath, Sections(Section("cw", "continue_watching", 4,
            Item("movie-1"), Item("episode-1", "episode", "series-1"), Item("series-2", "series"), Item("book-1", "audiobook"))));
        var home = Create();

        await home.ActivateAsync();

        var row = Assert.Single(home.Rows);
        Assert.Equal(["movie-1", "episode-1", "series-2"], row.Cards.Select(c => c.Id));
        Assert.All(row.Cards, card => Assert.Equal(MediaCardShape.Landscape, card.Shape));
    }

    [Fact]
    public async Task ContinueWatchingOfOnlyBooks_IsHidden()
    {
        _silo.Map(SectionsPath, Sections(Section("cw", "continue_watching", 1, Item("book-1", "audiobook")), Section("r", "genre", 1, Item("m1"))));
        var home = Create();

        await home.ActivateAsync();

        Assert.Equal(["r"], home.Rows.Select(r => r.SectionId));
        Assert.Equal(MediaCardShape.Poster, home.Rows[0].Cards[0].Shape);
    }

    [Fact]
    public async Task ExploreAll_OnlyWhenTheRowHasMoreThanItShows()
    {
        _silo.Map(SectionsPath, Sections(
            Section("tv", "recently_added", 240, Item("s1", "series"), Item("s2", "series")),
            Section("all", "genre", 2, Item("m1"), Item("m2"))));
        var home = Create();
        await home.ActivateAsync();

        Assert.Null(home.Rows[1].ExploreAllCommand);
        home.Rows[0].ExploreAllCommand!.Execute(null);

        var (route, parameter) = Assert.Single(_navigation.Navigations);
        Assert.Equal(AppRoute.Section, route);
        Assert.Equal(new SectionRouteParameter("tv", "Row tv", null, "recently_added"), parameter);
    }

    [Fact]
    public async Task Card_OpensTheItem()
    {
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 1, Item("movie-9"))));
        var home = Create();
        await home.ActivateAsync();

        home.Rows[0].OpenCardCommand.Execute(home.Rows[0].Cards[0]);

        Assert.Equal((AppRoute.Item, (object?)"movie-9"), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public async Task Artwork_IsAskedForAtTheDisplayScaleWithTheSameWidthsForRefetchedRows()
    {
        _silo.Map(SectionsPath, Sections(Section("lazy", "genre", 1)));
        _silo.Map(SectionsPath + "/lazy/items", Serialize(Section("lazy", "genre", 1, Item("m1"))));
        var home = Create();
        home.DisplayScale = 1.5;

        await home.ActivateAsync();

        var list = Assert.Single(_silo.To(SectionsPath));
        var row = Assert.Single(_silo.To(SectionsPath + "/lazy/items"));
        Assert.Equal("513", list.Query("poster_width"));
        Assert.Equal("1920", list.Query("backdrop_width"));
        Assert.Equal(list.Uri.Query, row.Uri.Query);
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/m1/poster.webp?exp=1&sig=s"), home.Rows[0].Cards[0].ImageUrl);
    }

    [Fact]
    public async Task FailedLoad_ShowsAFriendlyErrorAndRetryRecovers()
    {
        _silo.Map(SectionsPath, _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable, TimeSpan.FromSeconds(5)));
        var home = Create();

        await home.ActivateAsync();

        Assert.True(home.HasError);
        Assert.Empty(home.Rows);
        Assert.Equal("The server is busy. Try again in a moment.", home.ErrorText);
        Assert.Equal(home.ErrorText, home.StatusText);
        Assert.DoesNotContain("Silo", home.ErrorText, StringComparison.Ordinal);

        _silo.Map(SectionsPath, Sections(Section("r", "genre", 1, Item("m1"))));
        await home.RetryCommand.ExecuteAsync(null);

        Assert.False(home.HasError);
        Assert.Single(home.Rows);
        Assert.Equal("", home.StatusText);
    }

    [Fact]
    public async Task UnreachableServer_SaysSoWithoutTheExceptionText()
    {
        _silo.Map(SectionsPath, (_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "No such host is known. (silo.test:443)")));
        var home = Create();

        await home.ActivateAsync();

        Assert.True(home.HasError);
        Assert.Equal("Could not reach the server. Check the connection and try again.", home.ErrorText);
    }

    [Fact]
    public async Task NoRowWithCards_ShowsNothingHereYet()
    {
        _silo.Map(SectionsPath, Sections(Section("nothing", "genre", 0)));
        var home = Create();

        await home.ActivateAsync();

        Assert.True(home.IsEmpty);
        Assert.False(home.HasError);
        Assert.Equal("Nothing here yet", home.StatusText);
    }

    [Fact]
    public async Task AnotherProfile_ReloadsThroughTheSessionEvent()
    {
        int calls = 0;
        _silo.Map(SectionsPath, _ => BrowseFakeSilo.Json(Sections(Section("r", "genre", 1, Item("m" + Interlocked.Increment(ref calls))))));
        var home = Create();
        await home.ActivateAsync();
        Assert.Equal("m1", home.Rows[0].Cards[0].Id);

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;

        Assert.Equal("m2", home.Rows[0].Cards[0].Id);
        Assert.Equal(2, _silo.To(SectionsPath).Count);
    }

    [Fact]
    public async Task AccountUpdates_DoNotReload()
    {
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 1, Item("m1"))));
        var home = Create();
        await home.ActivateAsync();

        _session.RaiseChanged();
        await home.Loading;

        Assert.Single(_silo.To(SectionsPath));
    }

    [Fact]
    public async Task LeavingThePage_StopsFollowingTheSessionUntilItIsShownAgain()
    {
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 1, Item("m1"))));
        var home = Create();
        await home.ActivateAsync();
        home.Deactivate();

        _session.ProfileId = "8";
        _session.RaiseChanged();
        Assert.Single(_silo.To(SectionsPath));

        await home.ActivateAsync();
        Assert.Equal(2, _silo.To(SectionsPath).Count);
    }

    [Fact]
    public async Task ComingBack_ReloadsOnlyWhenTheRowsAreStale()
    {
        int calls = 0;
        _silo.Map(SectionsPath, _ => BrowseFakeSilo.Json(Sections(Section("r", "genre", 1, Item("m" + Interlocked.Increment(ref calls))))));
        var home = Create();
        await home.ActivateAsync();

        home.Deactivate();
        _clock.Now += HomeViewModel.StaleAfter - TimeSpan.FromSeconds(1);
        await home.ActivateAsync();
        Assert.Single(_silo.To(SectionsPath));

        home.Deactivate();
        _clock.Now += TimeSpan.FromSeconds(1);
        await home.ActivateAsync();
        Assert.Equal(2, _silo.To(SectionsPath).Count);
        Assert.Equal("m2", home.Rows[0].Cards[0].Id);
    }

    [Fact]
    public async Task StaleRefresh_KeepsTheShownRowsWhenItFails()
    {
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 1, Item("m1"))));
        var home = Create();
        await home.ActivateAsync();
        _silo.Map(SectionsPath, _ => BrowseFakeSilo.Problem(500, "internal_error"));
        _clock.Now += HomeViewModel.StaleAfter;

        home.Deactivate();
        await home.ActivateAsync();

        Assert.Equal("m1", Assert.Single(home.Rows).Cards[0].Id);
        Assert.False(home.HasError);
    }

    [Fact]
    public async Task NoProfile_ShowsNothingAndAsksNothing()
    {
        _session.State = SessionState.ProfileRequired;
        _session.ProfileId = null;
        var home = Create();

        await home.ActivateAsync();

        Assert.Empty(home.Rows);
        Assert.Empty(_silo.Requests);
        Assert.Equal("", home.StatusText);
    }

    [Fact]
    public async Task Remove_DismissesPerCardAndDropsTheWholeShowFromBothRows()
    {
        _silo.Map(SectionsPath, Sections(
            Section("cw", "continue_watching", 2,
                Item("episode-1", "episode", "series-1", "2026-01-02T03:04:05.000Z"), Item("movie-1", progressUpdatedAt: "2026-01-03T00:00:00.000Z")),
            Section("next", "next_up", 1, Item("episode-2", "episode", "series-1")),
            Section("recent", "recently_added", 1, Item("movie-2"))));
        _silo.Map("api/v2/home/dismissals/continue_watching/episode-1", _ => BrowseFakeSilo.NoContent());
        var home = Create();
        await home.ActivateAsync();
        var episode = home.Rows[0].Cards[0];

        Assert.True(home.CanRemove(episode));
        Assert.False(home.CanRemove(home.Rows[2].Cards[0]));
        await home.RemoveCommand.ExecuteAsync(episode);

        var dismissal = Assert.Single(_silo.To("api/v2/home/dismissals/continue_watching/episode-1"));
        Assert.Equal(HttpMethod.Put, dismissal.Method);
        Assert.Contains("\"progress_updated_at\":\"2026-01-02T03:04:05", dismissal.Body, StringComparison.Ordinal);
        Assert.Equal(["cw", "recent"], home.Rows.Select(r => r.SectionId));
        Assert.Equal(["movie-1"], home.Rows[0].Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task Remove_WithAnEmptySeriesId_RemovesOnlyThatCard()
    {
        _silo.Map(SectionsPath, Sections(Section("cw", "continue_watching", 2,
            Item("movie-1", seriesId: "", progressUpdatedAt: "2026-01-02T00:00:00.000Z"),
            Item("movie-2", seriesId: "", progressUpdatedAt: "2026-01-03T00:00:00.000Z"))));
        _silo.Map("api/v2/home/dismissals/continue_watching/movie-1", _ => BrowseFakeSilo.NoContent());
        var home = Create();
        await home.ActivateAsync();

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        Assert.Equal(["movie-2"], home.Rows[0].Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task SupersededLoad_LeavesItsFailureOut()
    {
        var firstAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        _silo.Map(SectionsPath, async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await firstAnswer.Task;
                throw new HttpRequestException(HttpRequestError.ConnectionError, "connection reset");
            }
            return BrowseFakeSilo.Json(Sections(Section("r", "genre", 1, Item("m1"))));
        });
        var home = Create();
        var first = home.ActivateAsync();

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;
        firstAnswer.SetResult();
        await first;

        Assert.False(home.HasError);
        Assert.Equal("m1", Assert.Single(home.Rows).Cards[0].Id);
        Assert.Equal("", home.StatusText);
    }

    [Fact]
    public async Task RowWithoutAnId_IsNeitherFetchedNorExplored()
    {
        _silo.Map(SectionsPath, Sections(Section("", "genre", 5), Section("r", "genre", 9, Item("m1"), Item(""))));
        var home = Create();

        await home.ActivateAsync();

        var row = Assert.Single(home.Rows);
        Assert.Equal(["m1"], row.Cards.Select(c => c.Id));
        Assert.NotNull(row.ExploreAllCommand);
        Assert.False(home.HasError);
    }

    [Fact]
    public async Task NextUpCard_IsDismissedFromNextUp()
    {
        _silo.Map(SectionsPath, Sections(Section("next", "next_up", 1, Item("episode-2", "episode", "series-1"))));
        _silo.Map("api/v2/home/dismissals/next_up/episode-2", _ => BrowseFakeSilo.NoContent());
        var home = Create();
        await home.ActivateAsync();

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        Assert.Contains("\"series_id\":\"series-1\"", Assert.Single(_silo.To("api/v2/home/dismissals/next_up/episode-2")).Body, StringComparison.Ordinal);
        Assert.True(home.IsEmpty);
    }

    [Fact]
    public async Task FailedRemove_KeepsTheCardAndSaysSo()
    {
        _silo.Map(SectionsPath, Sections(Section("cw", "continue_watching", 1, Item("movie-1", progressUpdatedAt: "2026-01-03T00:00:00.000Z"))));
        _silo.Map("api/v2/home/dismissals/continue_watching/movie-1", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var home = Create();
        await home.ActivateAsync();

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        Assert.Single(home.Rows[0].Cards);
        Assert.Equal("Title movie-1 could not be removed. Try again.", home.Notice);
    }

    [Fact]
    public async Task Logs_NeverCarryArtworkSignatures()
    {
        _silo.Map(SectionsPath, Sections(Section("lazy", "genre", 1)));
        _silo.Map(SectionsPath + "/lazy/items", _ => BrowseFakeSilo.Problem((int)HttpStatusCode.BadGateway, "bad_gateway"));
        var home = Create();

        await home.ActivateAsync();

        Assert.DoesNotContain("sig=", _silo.Logs.All, StringComparison.Ordinal);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public async Task AReloadedRow_TakesItsNewOrder_ReplacingChangedCardsWhereTheyAre()
    {
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 3, Item("a"), Item("b"), Item("c"))));
        var home = Create();
        await home.ActivateAsync();
        var row = home.Rows[0];
        var cardA = row.Cards[0];
        _silo.Map(SectionsPath, Sections(Section("r", "genre", 3, Item("c"), Item("d"), Item("a", progressUpdatedAt: "2026-10-25T10:00:00Z"))));

        _changes.Report();
        await home.Loading;

        Assert.Same(row, home.Rows[0]);
        Assert.Equal(["c", "d", "a"], row.Cards.Select(c => c.Id));
        Assert.NotEqual(cardA, row.Cards[2]);
        Assert.Equal(row.Cards[2], Assert.Single(home.Rows).Cards.Last());
    }

    [Fact]
    public async Task WatchingRows_OfferTheirCardsRemoval_ByTheSurfacesName()
    {
        _silo.Map(SectionsPath, Sections(
            Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Section("next", "next_up", 1, Item("e2", "episode", "s1")),
            Section("recent", "recently_added", 1, Item("r1"))));
        var home = Create();

        await home.ActivateAsync();

        Assert.Equal("Remove from Continue Watching", home.Rows[0].RemoveLabel);
        Assert.True(home.Rows[0].RemoveCardCommand!.CanExecute(home.Rows[0].Cards[0]));
        Assert.Equal("Remove from Next Up", home.Rows[1].RemoveLabel);
        Assert.Null(home.Rows[2].RemoveCardCommand);
        Assert.False(home.RemoveCommand.CanExecute(home.Rows[2].Cards[0]));
    }
}
