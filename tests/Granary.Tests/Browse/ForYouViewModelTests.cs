using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>For You and its rows in full, as Silo's web shows them.</summary>
public sealed class ForYouViewModelTests
{
    private const string DiscoverPath = "api/v2/recommendations/discover";
    private const string TastePath = "api/v2/recommendations/taste-profile";

    private readonly LibraryHarness _harness = new();

    public ForYouViewModelTests()
    {
        _harness.Answer(DiscoverPath, RecommendationPayloads.Discover);
        _harness.Answer(TastePath, RecommendationPayloads.Taste);
    }

    private ImageParameterCache Images() => new(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>());

    private ForYouViewModel Create() => new(
        _harness.Session, new RecommendationsApi(_harness.Silo.Http), Images(), _harness.Navigation, _harness.Logs.For<ForYouViewModel>());

    private RecommendationSectionViewModel CreateSection() => new(
        _harness.Session, new RecommendationsApi(_harness.Silo.Http), Images(), _harness.Navigation, _harness.Logs.For<RecommendationSectionViewModel>());

    private async Task<ForYouViewModel> LoadedAsync()
    {
        var page = Create();
        await page.ActivateAsync(isReturn: false);
        return page;
    }

    [Fact]
    public async Task TheRows_ShowWithTheirCards_EachOnce_AndEmptyRowsLeftOut()
    {
        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(["Because you like crime", "Popular on This Server", "Coming Up"], page.Rows.Select(r => r.Title));
        Assert.Equal(["Heat", "Alien"], page.Rows[0].Cards.Select(c => c.Title));
        Assert.StartsWith(LibraryHarness.ImageQuery, _harness.Queries(DiscoverPath)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowFromTheProfilesHistory_MakesThePagePersonal()
    {
        var page = await LoadedAsync();
        Assert.Equal(ForYouViewModel.PersonalSubtitle, page.Subtitle);

        _harness.Answer(DiscoverPath, RecommendationPayloads.DiscoverShared);
        await page.ActivateAsync(isReturn: false);
        Assert.Equal(ForYouViewModel.DefaultSubtitle, page.Subtitle);
    }

    [Fact]
    public async Task TheTasteProfile_ShowsBesideTheTitle_AndAnEmptyOneNot()
    {
        var page = await LoadedAsync();
        Assert.Equal(["Crime", "Thriller"], page.TopGenres);
        Assert.Equal(["Michael Mann"], page.FavoriteDirectors);
        Assert.True(page.HasTasteProfile);

        _harness.Answer(TastePath, RecommendationPayloads.NoTaste);
        await page.ActivateAsync(isReturn: false);
        Assert.False(page.HasTasteProfile);
    }

    [Fact]
    public async Task ATasteProfileThatFails_LeavesTheRows()
    {
        _harness.Answer(TastePath, ScriptedSilo.Problem(500, "internal"));

        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.False(page.HasTasteProfile);
        Assert.Equal(3, page.Rows.Count);
    }

    [Fact]
    public async Task NoRows_SaysNothingToShowYet()
    {
        _harness.Answer(DiscoverPath, RecommendationPayloads.NoRows);

        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Empty, page.State);
        Assert.Equal("", page.Subtitle);
    }

    [Fact]
    public async Task RowsThatFail_CanBeTriedAgain()
    {
        _harness.Answer(DiscoverPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, RecommendationPayloads.Discover));

        var page = await LoadedAsync();
        Assert.Equal(LibraryLoadState.Error, page.State);

        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal(LibraryLoadState.Ready, page.State);
    }

    [Fact]
    public async Task AQuietReloadThatFails_KeepsTheRowsShown()
    {
        var page = await LoadedAsync();
        _harness.Answer(DiscoverPath, ScriptedSilo.NetworkError());

        await page.ActivateAsync(isReturn: false);

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(3, page.Rows.Count);
    }

    [Fact]
    public async Task AQuietReloadWhoseTasteFails_KeepsTheTasteShown()
    {
        var page = await LoadedAsync();
        _harness.Answer(TastePath, ScriptedSilo.Problem(500, "internal"));

        await page.ActivateAsync(isReturn: false);

        Assert.Equal(["Crime", "Thriller"], page.TopGenres);
    }

    [Fact]
    public async Task TheSameRowsAgain_StayAsTheyAre()
    {
        var page = await LoadedAsync();
        int changes = 0;
        page.Rows.CollectionChanged += (_, _) => changes++;

        await page.ActivateAsync(isReturn: false);
        Assert.Equal(0, changes);

        _harness.Answer(DiscoverPath, RecommendationPayloads.DiscoverShared);
        await page.ActivateAsync(isReturn: false);
        Assert.NotEqual(0, changes);
        Assert.Single(page.Rows);
    }

    [Fact]
    public async Task AnUnexpectedFailure_EndsInTheErrorState()
    {
        _harness.Answer(DiscoverPath, () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("boom")));

        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Error, page.State);
    }

    [Fact]
    public async Task AnEarlierLoad_AnsweringLate_LeavesTheNewerOneShown()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(DiscoverPath, LibraryHarness.Held(release, RecommendationPayloads.Discover),
            ScriptedSilo.Json(HttpStatusCode.OK, RecommendationPayloads.DiscoverShared));
        var page = Create();
        var first = page.ActivateAsync(isReturn: false);

        await page.ActivateAsync(isReturn: false);
        release.SetResult();
        await first;

        Assert.Equal(["Popular on This Server"], page.Rows.Select(r => r.Title));
        Assert.Equal(ForYouViewModel.DefaultSubtitle, page.Subtitle);
    }

    [Fact]
    public async Task AnotherProfile_LoadsItsOwnRows_EvenOnBack()
    {
        var page = await LoadedAsync();
        _harness.Session.ProfileId = "8";
        _harness.Answer(DiscoverPath, RecommendationPayloads.DiscoverShared);

        await page.ActivateAsync(isReturn: true);

        Assert.Equal(2, _harness.Count(DiscoverPath));
        Assert.Equal(["Popular on This Server"], page.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task BackAfterAFailure_TriesAgain()
    {
        _harness.Answer(DiscoverPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, RecommendationPayloads.Discover));
        var page = await LoadedAsync();

        await page.ActivateAsync(isReturn: true);

        Assert.Equal(LibraryLoadState.Ready, page.State);
    }

    [Fact]
    public async Task ASectionOpenedAgain_IsReadAgain_ButBackKeepsIt()
    {
        _harness.Answer("api/v2/recommendations/section/popular", RecommendationPayloads.Section);
        var section = CreateSection();
        var popular = new RecommendationSectionRouteParameter("popular", null, "Popular");
        await section.ShowAsync(popular);

        await section.ShowAsync(popular, isReturn: true);
        Assert.Equal(1, _harness.Count("api/v2/recommendations/section/popular"));

        await section.ShowAsync(popular);
        Assert.Equal(2, _harness.Count("api/v2/recommendations/section/popular"));
    }

    [Fact]
    public async Task Back_KeepsThePage()
    {
        var page = await LoadedAsync();

        await page.ActivateAsync(isReturn: true);

        Assert.Equal(1, _harness.Count(DiscoverPath));
    }

    [Fact]
    public async Task ExploreAll_OpensTheRowsSection_OnlyWhereOneListsIt()
    {
        var page = await LoadedAsync();

        Assert.Null(page.Rows[2].ExploreAllCommand);
        page.Rows[0].ExploreAllCommand!.Execute(null);
        page.Rows[1].OpenCardCommand.Execute(page.Rows[1].Cards[0]);

        Assert.Equal(
        [
            (AppRoute.RecommendationSection, new RecommendationSectionRouteParameter("cluster", "0", "Because you like crime")),
            (AppRoute.Item, (object?)"movie:alien-1979"),
        ], _harness.Navigation.Navigations.Select(n => (n.Route, n.Parameter)).ToList<(AppRoute, object?)>());
    }

    [Fact]
    public async Task ASection_ShowsTheRowInFull_WithItsCount()
    {
        _harness.Answer("api/v2/recommendations/section/popular", RecommendationPayloads.Section);
        var section = CreateSection();

        await section.ShowAsync(new RecommendationSectionRouteParameter("popular", null, "Popular"));

        Assert.Equal("Popular on This Server", section.Title);
        Assert.Equal("2 titles", section.CountText);
        Assert.Equal(["Alien", "Heat"], section.Cards.Select(c => c.Title));
        Assert.StartsWith(LibraryHarness.ImageQuery, _harness.Queries("api/v2/recommendations/section/popular")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASectionWithAKey_AsksForIt_AndNamesItselfWhenTheAnswerDoesNot()
    {
        _harness.Answer("api/v2/recommendations/section/genre", RecommendationPayloads.UntitledSection);
        var section = CreateSection();

        await section.ShowAsync(new RecommendationSectionRouteParameter("genre", "Crime", ""));

        Assert.StartsWith("key=Crime&", _harness.Queries("api/v2/recommendations/section/genre")[0], StringComparison.Ordinal);
        Assert.Equal("Top Crime", section.Title);
        Assert.Equal(LibraryLoadState.Empty, section.State);
    }

    [Fact]
    public async Task AnUnknownSection_AsksNothing()
    {
        var section = CreateSection();

        await section.ShowAsync(new RecommendationSectionRouteParameter("../admin", null, "x"));

        Assert.DoesNotContain(_harness.Silo.Sent, r => r.Uri.AbsolutePath.Contains("recommendations", StringComparison.Ordinal));
        Assert.Equal(LibraryLoadState.Idle, section.State);
    }

    [Fact]
    public async Task TheApi_RefusesAKindItDoesNotKnow()
    {
        var api = new RecommendationsApi(_harness.Silo.Http);

        await Assert.ThrowsAsync<ArgumentException>(() => api.GetSectionAsync(ScriptedSilo.Base, "../admin", cancellationToken: TestContext.Current.CancellationToken));
    }
}
