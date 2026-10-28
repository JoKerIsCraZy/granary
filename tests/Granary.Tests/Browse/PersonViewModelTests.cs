using Granary.Core.Abstractions;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>The person page: who they are, their titles newest first, and a refresh of details the provider has not given yet.</summary>
public sealed class PersonViewModelTests
{
    private const string PersonPath = "api/v2/catalog/people/7";
    private const string RefreshPath = "api/v2/catalog/people/7/refresh";
    private const string AdminRefreshPath = "api/v2/admin/people/7/refresh";
    private const string CatalogPath = "api/v2/catalog";

    private const string AlPacino = """
        { "id": "7", "name": "Al Pacino", "photo_url": "/api/v2/artwork/people/7.webp?sig=p",
          "bio": "An American actor.", "birth_date": "1940-04-25", "birthplace": "New York City, New York, USA" }
        """;

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    // 1 October 2026, 12:00 UTC.
    private readonly ManualTimeProvider _time = new();

    public PersonViewModelTests()
    {
        _silo.Map(PersonPath, AlPacino);
        _silo.Map(CatalogPath, request => BrowseFakeSilo.Json(request.RequestUri!.Query.Contains("cursor=p2", StringComparison.Ordinal)
            ? Page(null, Item("serpico-1973"))
            : Page("p2", Item("irishman-2019"), Item("heat-1995"))));
        _silo.Map(RefreshPath, _ => BrowseFakeSilo.Json("""{ "person_id": "7", "status": "queued" }""", System.Net.HttpStatusCode.Accepted));
    }

    private PersonViewModel Create() => new(_session, _silo.Catalog, new AdminApi(_silo.Http), _silo.Images,
        new AccountPermissions(_session, new ProfilesApi(_silo.Http), _silo.Logs.For<AccountPermissions>()), _navigation, _time,
        _silo.Logs.For<PersonViewModel>());

    private async Task<PersonViewModel> ShownAsync(string id = "7")
    {
        var person = Create();
        await person.ShowAsync(id, isReturn: false);
        return person;
    }

    [Fact]
    public async Task ThePerson_ShowsAsTheWebWordsThem()
    {
        var person = await ShownAsync();

        Assert.True(person.IsReady);
        Assert.Equal("Al Pacino", person.Name);
        Assert.Equal(["Born Apr 25, 1940", "86 years old", "New York City, New York, USA"], person.Facts);
        Assert.Equal("An American actor.", person.Bio);
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/people/7.webp?sig=p"), person.PhotoUrl);
        Assert.Equal("AP", person.Initials);
        // Everything is known: nothing to refresh.
        Assert.Empty(_silo.To(RefreshPath));
    }

    [Fact]
    public void Facts_SayWhenTheyDied_AndTheirAgeThen()
    {
        var facts = PersonFacts.Of(new Granary.Core.Api.Catalog.Person { BirthDate = "1920-03-01", DeathDate = "2005-02-28" }, new DateOnly(2026, 10, 1));

        Assert.Equal(["Born Mar 1, 1920", "Died Feb 28, 2005 (age 84)"], facts);
    }

    [Fact]
    public void Facts_KeepADateTheyCannotRead()
    {
        var facts = PersonFacts.Of(new Granary.Core.Api.Catalog.Person { BirthDate = "1940" }, new DateOnly(2026, 10, 1));

        Assert.Equal(["Born 1940"], facts);
    }

    [Fact]
    public async Task TheFilmography_ComesNewestFirst_AndPagesAsTheGridScrolls()
    {
        var person = await ShownAsync();

        var first = _silo.To(CatalogPath)[0];
        Assert.Equal("person", first.Query("source"));
        Assert.Equal("7", first.Query("person_id"));
        Assert.Equal("-year", first.Query("sort"));
        Assert.Equal("60", first.Query("limit"));
        Assert.Null(first.Query("type"));
        Assert.Equal(["irishman-2019", "heat-1995"], person.Cards.Select(c => c.Id));

        await person.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["irishman-2019", "heat-1995", "serpico-1973"], person.Cards.Select(c => c.Id));
    }

    [Theory]
    [InlineData(1, "movie")]
    [InlineData(2, "series")]
    public async Task AFilter_KeepsOneKindOfTitle(int filter, string type)
    {
        var person = await ShownAsync();

        person.Filter = person.Filters[filter];
        await person.Loading;

        Assert.Equal(type, _silo.To(CatalogPath)[^1].Query("type"));
        Assert.Equal(["All", "Movies", "Series"], person.Filters.Select(f => f.Label));
    }

    [Fact]
    public async Task ACard_OpensItsTitle()
    {
        var person = await ShownAsync();

        person.OpenCardCommand.Execute(person.Cards[0]);

        Assert.Equal((AppRoute.Item, (object?)"irishman-2019"), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public async Task APersonNoLongerThere_SaysSo()
    {
        _silo.Map(PersonPath, _ => BrowseFakeSilo.Problem(404, "not_found"));

        var person = await ShownAsync();

        Assert.True(person.HasError);
        Assert.Equal("This person isn't available", person.ErrorTitle);
        Assert.Equal("They may have been removed from the catalog, or the link may be wrong.", person.ErrorText);
        Assert.False(person.CanRetry);
    }

    [Fact]
    public async Task AFailedRead_OffersRetry()
    {
        _silo.Map(PersonPath, _ => BrowseFakeSilo.Problem(503, "dependency_unavailable"));
        var person = await ShownAsync();
        Assert.Equal("Couldn't load this person", person.ErrorTitle);
        Assert.True(person.CanRetry);

        _silo.Map(PersonPath, AlPacino);
        await person.RetryCommand.ExecuteAsync(null);

        Assert.True(person.IsReady);
    }

    [Fact]
    public async Task Back_KeepsThePage_AnotherPersonReadsAgain()
    {
        var person = await ShownAsync();

        await person.ShowAsync("7", isReturn: true);
        Assert.Single(_silo.To(PersonPath));

        _silo.Map("api/v2/catalog/people/8", """{ "id": "8", "name": "Robert De Niro", "bio": "b", "photo_url": "/p.webp", "birth_date": "1943-08-17" }""");
        await person.ShowAsync("8", isReturn: false);
        Assert.Equal("Robert De Niro", person.Name);
    }

    [Fact]
    public async Task MissingDetails_AreRefreshedOnce_AndReadAgainUntilTheyArrive()
    {
        int reads = 0;
        _silo.Map(PersonPath, _ => BrowseFakeSilo.Json(++reads < 3
            ? """{ "id": "7", "name": "Al Pacino" }"""
            : AlPacino));

        var person = await ShownAsync();
        Assert.Single(_silo.To(RefreshPath));
        Assert.True(person.IsRefreshing);

        await AdvanceAsync(PersonViewModel.RefreshPollInterval);
        Assert.Equal("", person.Bio);
        await AdvanceAsync(PersonViewModel.RefreshPollInterval);

        Assert.Equal("An American actor.", person.Bio);
        Assert.False(person.IsRefreshing);
        await AdvanceAsync(PersonViewModel.RefreshPollInterval);
        Assert.Equal(3, _silo.To(PersonPath).Count);
        Assert.Single(_silo.To(RefreshPath));
    }

    [Fact]
    public async Task ARefreshThatChangesNothing_IsNotFollowedForever()
    {
        _silo.Map(PersonPath, """{ "id": "7", "name": "Al Pacino" }""");
        var person = await ShownAsync();

        for (int i = 0; i < 8; i++)
        {
            await AdvanceAsync(PersonViewModel.RefreshPollInterval);
        }

        // The first read, then five that changed nothing.
        Assert.Equal(1 + PersonViewModel.MaxUnchangedReads, _silo.To(PersonPath).Count);
        Assert.False(person.IsRefreshing);
    }

    [Fact]
    public async Task RefreshMetadata_QueuesARefresh()
    {
        var person = await ShownAsync();
        Assert.Equal("Refresh metadata", person.RefreshLabel);

        await person.RefreshCommand.ExecuteAsync(null);

        Assert.Single(_silo.To(RefreshPath));
        Assert.Equal("Person refresh queued", person.RefreshNotice);
    }

    [Fact]
    public async Task AnAdministrator_RefreshesNow()
    {
        _session.Account = new Account("1", "elena", "", "admin", [], true, false, null);
        _silo.Map("api/v2/profiles", """{ "items": [ { "id": "7", "name": "Elena", "is_primary": true } ], "avatar_upload_enabled": false }""");
        _silo.Map(AdminRefreshPath, AlPacino.Replace("An American actor.", "Refreshed.", StringComparison.Ordinal));
        var person = await ShownAsync();
        Assert.Equal("Refresh now", person.RefreshLabel);

        await person.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(System.Net.Http.HttpMethod.Post, Assert.Single(_silo.To(AdminRefreshPath)).Method);
        Assert.Equal("Refreshed.", person.Bio);
        Assert.Equal("Person metadata refreshed", person.RefreshNotice);
    }

    [Fact]
    public async Task AFailedRefresh_SaysSo()
    {
        _silo.Map(RefreshPath, _ => BrowseFakeSilo.Problem(503, "dependency_unavailable"));
        var person = await ShownAsync();

        await person.RefreshCommand.ExecuteAsync(null);

        Assert.StartsWith("Refresh failed", person.RefreshNotice, StringComparison.Ordinal);
        Assert.True(person.RefreshFailed);
    }

    // Waits for the page's next timer (none once it stopped following), moves the clock, and lets the read it set off answer.
    private async Task AdvanceAsync(TimeSpan by)
    {
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 50 && _time.NextDue is null; i++)
        {
            await Task.Delay(5, ct);
        }
        _time.Advance(by);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(5, ct);
        }
    }
}
