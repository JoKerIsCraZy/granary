using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryGridWindowTests
{
    private const string FirstQuery = "library_id=1&sort=title&limit=60";

    private static readonly CatalogQuery Query = new() { LibraryId = "1", Sort = CatalogSort.Ascending(CatalogSortFields.Title), Limit = 60 };

    private readonly LogSink _logs = new();

    [Fact]
    public async Task FirstPage_HoldsAPlaceForEveryTitle_AndTheNextPageContinuesFromItsCursor()
    {
        var listing = new Listing(300);
        var grid = Grid(listing);

        await grid.StartAsync(ScriptedSilo.Base, Query);
        await AuthPipeline.Eventually(() => IsLoaded(grid, 60, 119));

        Assert.Equal(300, grid.Cards.Count);
        Assert.Equal("T0000", grid.Cards[0].Title);
        Assert.Equal("T0119", grid.Cards[119].Title);
        Assert.True(grid.Cards[120].IsPlaceholder);
        Assert.Equal("300 titles", grid.TotalText);
        // Only the first page counts the titles, as the web client asks.
        Assert.Equal([FirstQuery, FirstQuery + "&cursor=n60&skip_total=true"], listing.Queries);
    }

    [Fact]
    public async Task FarDown_SeeksThePagesAroundTheView_WithoutTheOnesBefore()
    {
        var listing = new Listing(1000);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        await AuthPipeline.Eventually(() => IsLoaded(grid, 60, 119));

        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => IsLoaded(grid, 540, 719));

        // Each page once; one after a page already loaded may continue from its cursor instead of seeking.
        Assert.Equal([0, 60, 540, 600, 660], listing.Positions.Order());
        Assert.Contains(FirstQuery + "&cursor=w&seek=540&skip_total=true", listing.Queries);
        Assert.Equal("T0600", grid.Cards[600].Title);
        Assert.True(grid.Cards[300].IsPlaceholder);
    }

    [Fact]
    public async Task AViewTold_WhileTheFirstPageLoads_LoadsItsWindowAfterIt()
    {
        var listing = new Listing(1000);
        var hold = listing.Hold(0);
        var grid = Grid(listing);

        var start = grid.StartAsync(ScriptedSilo.Base, Query);
        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        hold.SetResult();
        await start;
        await AuthPipeline.Eventually(() => IsLoaded(grid, 540, 719));

        Assert.DoesNotContain(FirstQuery + "&cursor=n60&skip_total=true", listing.Queries);
    }

    [Fact]
    public async Task ShortLastPage_EndsTheListingThere()
    {
        // The first page counted 300; titles went away before the last page was read.
        var listing = new Listing(130, reportedTotal: 300);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        Assert.Equal(300, grid.Cards.Count);

        grid.ShowViewport(new GridViewport(120, 150, 124, IsAtEnd: false));
        await AuthPipeline.Eventually(() => grid.Cards.Count == 130 && IsLoaded(grid, 0, 129));

        Assert.Equal("130 titles", grid.TotalText);
    }

    [Fact]
    public async Task EstimatedTotal_GrowsByAPage_UntilAPageSaysWhereTheListingEnds()
    {
        var listing = new Listing(150, reportedTotal: 10, exact: false);
        var hold = listing.Hold(60);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        Assert.Equal(120, grid.Cards.Count);
        Assert.Equal("About 60 titles", grid.TotalText);

        hold.SetResult();
        await AuthPipeline.Eventually(() => grid.Cards.Count == 180);
        grid.ShowViewport(new GridViewport(120, 150, 124, IsAtEnd: false));
        await AuthPipeline.Eventually(() => grid.Cards.Count == 150 && IsLoaded(grid, 0, 149));

        Assert.Equal("150 titles", grid.TotalText);
    }

    [Fact]
    public async Task APageScrolledAway_IsNotWaitedFor_AndLoadsWhenItComesBack()
    {
        var listing = new Listing(1000);
        var hold = listing.Hold(600);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => IsLoaded(grid, 540, 599) && IsLoaded(grid, 660, 719));

        grid.ShowViewport(new GridViewport(0, 29, 4, IsAtEnd: false));
        await AuthPipeline.Eventually(() => listing.Cancelled.Contains(600));

        Assert.True(grid.Cards[600].IsPlaceholder);
        Assert.False(grid.HasPageError);
        Assert.DoesNotContain(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));

        listing.Release(600, hold);
        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => IsLoaded(grid, 600, 659));

        // The second time the page before it is loaded: it continues from that one's cursor.
        Assert.Equal(2, listing.Queries.Count(q => q.EndsWith("seek=600&skip_total=true", StringComparison.Ordinal) || q.EndsWith("cursor=n600&skip_total=true", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedPage_KeepsItsPlaces_UntilRetried()
    {
        var listing = new Listing(1000);
        listing.FailOnce(600, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);

        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => grid.HasPageError);

        Assert.True(grid.Cards[600].IsPlaceholder);
        Assert.True(grid.IsReady);

        grid.RetryPagesCommand.Execute(null);
        await AuthPipeline.Eventually(() => IsLoaded(grid, 600, 659));

        Assert.False(grid.HasPageError);
    }

    [Fact]
    public async Task ARefusedWindowCursor_StartsTheListingOver_Once()
    {
        var listing = new Listing(1000);
        listing.FailOnce(600, ScriptedSilo.Problem(400, ProblemCodes.InvalidCursor));
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        int restarts = 0;
        int changes = 0;
        grid.Restarted += (_, _) => restarts++;
        grid.ListingChanged += (_, _) => changes++;

        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => restarts == 1 && grid.IsReady && IsLoaded(grid, 0, 119));

        Assert.Equal(2, listing.Queries.Count(q => q == FirstQuery));
        Assert.Equal(1, changes);
        // Started over at the top: nothing more is asked for the window the refused page was in.
        Assert.Equal(1, listing.Queries.Count(q => q.Contains("seek=540", StringComparison.Ordinal)));
        Assert.False(grid.HasPageError);

        // A second refusal in the same listing is a failed page, not another start over.
        listing.FailOnce(600, ScriptedSilo.Problem(400, ProblemCodes.InvalidCursor));
        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => grid.HasPageError);

        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task AShortPage_WithMoreToFollow_IsCompletedFromItsOwnCursor()
    {
        var listing = new Listing(1000);
        listing.ShortAt(600, 40);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);

        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => IsLoaded(grid, 540, 719));

        Assert.Contains(FirstQuery + "&cursor=n640&skip_total=true", listing.Queries);
        Assert.Equal("T0659", grid.Cards[659].Title);
        Assert.Equal("T0660", grid.Cards[660].Title);
    }

    [Fact]
    public async Task AnUnexpectedFailure_MarksThePageFailed_ForRetry()
    {
        var listing = new Listing(1000);
        listing.FailOnce(600, () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("unexpected")));
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);

        grid.ShowViewport(new GridViewport(600, 629, 604, IsAtEnd: false));
        await AuthPipeline.Eventually(() => grid.HasPageError);

        Assert.Contains(_logs.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));

        grid.RetryPagesCommand.Execute(null);
        await AuthPipeline.Eventually(() => IsLoaded(grid, 600, 659));
    }

    [Fact]
    public async Task APageOfAReplacedListing_IsDropped()
    {
        var listing = new Listing(300);
        var hold = listing.Hold(60);
        var grid = Grid(listing);
        await grid.StartAsync(ScriptedSilo.Base, Query);
        var replaced = grid.Cards;
        listing.Release(60, hold);

        await grid.StartAsync(ScriptedSilo.Base, Query with { Sort = CatalogSort.Descending(CatalogSortFields.Title) });
        await AuthPipeline.Eventually(() => listing.Cancelled.Contains(60) && IsLoaded(grid, 60, 119));

        Assert.NotSame(replaced, grid.Cards);
        Assert.True(replaced.Skip(60).All(c => c.IsPlaceholder));
        Assert.False(grid.HasPageError);
        Assert.Equal(300, grid.Cards.Count);
    }

    [Fact]
    public void Placeholder_IsNothingToOpen()
    {
        Assert.True(MediaCard.Placeholder.IsPlaceholder);
        Assert.False(new MediaCard("movie:1", "movie", "Alien").IsPlaceholder);
    }

    private static bool IsLoaded(LibraryGridViewModel grid, int first, int last) =>
        grid.Cards.Count > last && Enumerable.Range(first, last - first + 1).All(i => !grid.Cards[i].IsPlaceholder);

    private LibraryGridViewModel Grid(Listing listing) =>
        new(new CatalogApi(new SiloHttp(new HttpClient(new Handler(listing.RespondAsync)))), _logs.For<LibraryGridViewModel>());

    /// <summary>
    /// Numbered titles behind <c>GET /api/v2/catalog</c>, paged as the server pages them: by a page's cursor ("n" and
    /// the position after it), or from the window cursor ("w") with a seek. A page can be held or fail once by its
    /// position.
    /// </summary>
    private sealed class Listing(int count, int? reportedTotal = null, bool exact = true)
    {
        private readonly ConcurrentQueue<string> _queries = new();
        private readonly ConcurrentQueue<int> _positions = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _holds = new();
        private readonly ConcurrentDictionary<int, Func<Task<HttpResponseMessage>>> _failures = new();
        private readonly ConcurrentDictionary<int, int> _short = new();

        public IReadOnlyList<string> Queries => [.. _queries];

        /// <summary>The list position each page was asked at.</summary>
        public IReadOnlyList<int> Positions => [.. _positions];

        public ConcurrentBag<int> Cancelled { get; } = [];

        public TaskCompletionSource Hold(int position) =>
            _holds[position] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Later asks for the position are not held; one already waiting still waits for <paramref name="hold"/>.</summary>
        public void Release(int position, TaskCompletionSource hold) => _holds.TryRemove(new(position, hold));

        public void FailOnce(int position, Func<Task<HttpResponseMessage>> failure) => _failures[position] = failure;

        /// <summary>The page asked at <paramref name="position"/> comes back with only <paramref name="items"/> titles and more to follow.</summary>
        public void ShortAt(int position, int items) => _short[position] = items;

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string query = request.RequestUri!.Query.TrimStart('?');
            _queries.Enqueue(query);
            var values = HttpUtility.ParseQueryString(query);
            int limit = int.Parse(values["limit"]!, CultureInfo.InvariantCulture);
            int position = values["seek"] is { } seek ? int.Parse(seek, CultureInfo.InvariantCulture)
                : values["cursor"] is { } cursor ? int.Parse(cursor[1..], CultureInfo.InvariantCulture)
                : 0;
            _positions.Enqueue(position);
            if (_holds.TryGetValue(position, out var hold))
            {
                try
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.Add(position);
                    throw;
                }
            }
            if (_failures.TryRemove(position, out var failure))
            {
                return await failure();
            }
            int end = Math.Min(count, position + (_short.TryGetValue(position, out int shortened) ? shortened : limit));
            var items = Enumerable.Range(position, Math.Max(0, end - position)).Select(i => string.Create(CultureInfo.InvariantCulture,
                $$"""{ "content_id": "movie:t{{i}}", "type": "movie", "title": "T{{i:0000}}", "genres": [], "keywords": [], "status": "matched" }"""));
            string page = end < count ? $$"""{ "next_cursor": "n{{end}}", "has_more": true }""" : """{ "has_more": false }""";
            string json = $$"""
                { "window_cursor": "w", "items": [{{string.Join(", ", items)}}], "page": {{page}},
                  "total": {{reportedTotal ?? count}}, "total_exact": {{(exact ? "true" : "false")}} }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
