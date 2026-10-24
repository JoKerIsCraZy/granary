using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryRowsTests
{
    private const string SectionsPath = "api/v2/library/1/sections";

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task Rows_HideEmptyOnesAndFetchRowsThatCameBackWithoutCards()
    {
        var library = _harness.Create();

        await library.ActivateAsync("1");

        var rows = library.Recommended.Rows;
        Assert.Equal(new[] { "Continue Watching", "Recently Added" }, rows.Select(r => r.Title));
        Assert.Equal(LibraryLoadState.Ready, library.Recommended.State);
        Assert.Equal(MediaCardShape.Landscape, Assert.Single(rows[0].Cards).Shape);
        Assert.Equal(new[] { "Alien", "Heat" }, rows[1].Cards.Select(c => c.Title));
        Assert.Equal(MediaCardShape.Poster, rows[1].Cards[0].Shape);
        Assert.Equal(LibraryHarness.ImageQuery, Assert.Single(_harness.Queries("api/v2/library/1/sections/recently_added/items")));
        Assert.Equal(0, _harness.Count("api/v2/library/1/sections/watchlist/items"));
    }

    [Fact]
    public async Task Hydration_RunsAtMostFourRowsAtOnce()
    {
        const int RowCount = 6;
        string sections = """{"sections": [""" + string.Join(",", Enumerable.Range(1, RowCount).Select(i =>
            $$"""{"id": "r{{i}}", "section_type": "genre", "title": "Row {{i}}", "featured": false, "item_limit": 20, "total_count": 5, "is_custom": false, "customized": false, "items": []}""")) + "]}";
        _harness.Answer(SectionsPath, sections);
        var release = new TaskCompletionSource();
        int inFlight = 0;
        int mostInFlight = 0;
        var gate = new Lock();
        for (int i = 1; i <= RowCount; i++)
        {
            string body = LibraryPayloads.RecentlyAddedItems.Replace("\"recently_added\"", $"\"r{i}\"", StringComparison.Ordinal);
            _harness.Answer($"api/v2/library/1/sections/r{i}/items", async () =>
            {
                lock (gate)
                {
                    mostInFlight = Math.Max(mostInFlight, ++inFlight);
                }
                await release.Task;
                lock (gate)
                {
                    inFlight--;
                }
                return await ScriptedSilo.Json(HttpStatusCode.OK, body)();
            });
        }
        var library = _harness.Create();

        var load = library.ActivateAsync("1");
        await Auth.AuthPipeline.Eventually(() => Volatile.Read(ref inFlight) == LibraryRowsViewModel.MaxConcurrentHydrations);
        Assert.Equal(LibraryLoadState.Loading, library.Recommended.State);
        release.SetResult();
        await load;

        Assert.Equal(LibraryRowsViewModel.MaxConcurrentHydrations, mostInFlight);
        Assert.All(library.Recommended.Rows, row => Assert.Equal(2, row.Cards.Count));
        Assert.Equal(LibraryLoadState.Ready, library.Recommended.State);
    }

    [Fact]
    public async Task ExploreAll_OpensTheRowWithTheLibrary()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");

        library.Recommended.Rows[1].ExploreAllCommand.Execute(null);

        var (route, parameter) = _harness.Navigation.Navigations[^1];
        Assert.Equal(AppRoute.Section, route);
        Assert.Equal(new SectionRouteParameter("recently_added", "Recently Added", "1"), parameter);
    }

    [Fact]
    public async Task Card_OpensItsTitle()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");
        var row = library.Recommended.Rows[0];

        row.OpenCardCommand.Execute(row.Cards[0]);

        Assert.Equal((AppRoute.Item, (object?)"movie:heat-1995"), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task FailedRowFetch_LeavesThatRowHidden()
    {
        _harness.Answer("api/v2/library/1/sections/recently_added/items", ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.False(library.Recommended.Rows[1].HasCards);
        Assert.True(library.Recommended.Rows[0].HasCards);
        Assert.Equal(LibraryLoadState.Ready, library.Recommended.State);
        Assert.Contains(_harness.Logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryRowFetchFailing_IsAnErrorNotAnEmptyLibrary()
    {
        const string ItemsPath = "api/v2/library/1/sections/recently_added/items";
        _harness.Answer(SectionsPath, """{"sections": [{"id": "recently_added", "section_type": "recently_added", "title": "Recently Added", "featured": false, "item_limit": 20, "total_count": 12, "is_custom": false, "customized": false, "items": []}]}""");
        _harness.Answer(ItemsPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable), ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.RecentlyAddedItems));
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.True(library.Recommended.HasError);
        Assert.False(library.Recommended.IsEmpty);
        Assert.Equal(LibraryErrors.Busy, library.Recommended.ErrorMessage);

        await library.Recommended.RetryCommand.ExecuteAsync(null);

        Assert.Equal(LibraryLoadState.Ready, library.Recommended.State);
        Assert.Equal(2, Assert.Single(library.Recommended.Rows).Cards.Count);
    }

    [Fact]
    public async Task NoRowsWithTitles_ShowsTheEmptyState()
    {
        _harness.Answer(SectionsPath, """{"sections": [{"id": "watchlist", "section_type": "watchlist", "title": "Your Watchlist", "featured": false, "item_limit": 20, "total_count": 0, "is_custom": false, "customized": false, "items": []}]}""");
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Empty(library.Recommended.Rows);
        Assert.True(library.Recommended.IsEmpty);
    }

    [Fact]
    public async Task FailedRows_ShowAFriendlyTextAndRetryRecovers()
    {
        _harness.Answer(SectionsPath, ScriptedSilo.Problem(500, "internal_error"), ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.Sections));
        var library = _harness.Create();
        await library.ActivateAsync("1");
        Assert.True(library.Recommended.HasError);
        Assert.Equal(LibraryErrors.Generic, library.Recommended.ErrorMessage);
        Assert.Equal(LibraryLoadState.Ready, library.State);

        await library.Recommended.RetryCommand.ExecuteAsync(null);

        Assert.Equal(LibraryLoadState.Ready, library.Recommended.State);
        Assert.Equal(2, library.Recommended.Rows.Count);
    }
}
