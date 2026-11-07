using Granary.Tests.Profiles;
using Microsoft.Extensions.Logging;
using static Granary.Tests.Auth.AuthPipeline;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// The profile's stars for a movie or series, as the web's <c>StarRating</c> sets them (docs/api-contract.md, 7.11): a
/// star sets that rating, the current star clears it, and a refused write puts the rating back.
/// </summary>
/// <remarks>A click's command ends once the title's writes are all done, so a test releases a held write before it waits.</remarks>
public sealed class ItemDetailRatingTests
{
    private readonly ItemDetailFixture _fixture = new();

    private static string Rating(string id) => "api/v2/ratings/" + id;

    private static string Rated(string payload, int stars) =>
        payload.Replace("\"user_state\":", $"\"user_rating\": {stars},\n  \"user_state\":", StringComparison.Ordinal);

    private ItemDetailFixture RatedMovie(int stars) =>
        _fixture.Get(ItemPath(MovieId), Ok(Rated(Movie, stars))).Get(SimilarPath(MovieId), Ok(MovieSimilar));

    private IReadOnlyList<SentRequest> Writes => [.. _fixture.Silo.Sent.Where(r => r.Method != HttpMethod.Get)];

    [Fact]
    public async Task TheStars_ComeWithTheTitle()
    {
        var detail = await RatedMovie(4).LoadAsync(MovieId);

        Assert.True(detail.CanRate);
        Assert.Equal(4, detail.UserRating);
        // No read of its own: the title's answer carries the rating, as the web reads it.
        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/ratings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnratedTitle_ShowsNoStarsFilled()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.True(detail.CanRate);
        Assert.Equal(0, detail.UserRating);
    }

    [Fact]
    public async Task AStar_SetsTheRating()
    {
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(3);

        Assert.Equal(3, detail.UserRating);
        Assert.False(detail.HasActionError);
        var sent = Assert.Single(Writes);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/silo/api/v2/ratings/movie-tmdb-1001", sent.Uri.AbsolutePath);
        Assert.Equal("""{"rating":3}""", sent.Body);
        Assert.Equal(Granary.Core.Api.SiloAuth.Profile, sent.Auth);
        // Made for the profile the page shows: a click still waiting when the profile changes is never sent as the next one.
        Assert.Equal("7", sent.AsProfile);
        // Home's rows and recommendations follow the rating.
        Assert.Equal(1, _fixture.Changes.Version);
    }

    [Fact]
    public async Task AnotherStar_ChangesTheRating()
    {
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(2);

        Assert.Equal(2, detail.UserRating);
        Assert.Equal("""{"rating":2}""", Assert.Single(Writes).Body);
    }

    [Fact]
    public async Task TheCurrentStar_ClearsTheRating()
    {
        RatedMovie(4).Silo.On(HttpMethod.Delete, Rating(MovieId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(4);

        Assert.Equal(0, detail.UserRating);
        var sent = Assert.Single(Writes);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Null(sent.Body);
        Assert.Equal("7", sent.AsProfile);
        Assert.Equal(1, _fixture.Changes.Version);
    }

    [Fact]
    public async Task ARefusedRating_PutsThePreviousOneBackAndSaysSo()
    {
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.Problem(500, "internal"));
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(2);

        Assert.Equal(4, detail.UserRating);
        // The web's words first, then the reason as the favourite and watchlist toggles give it.
        Assert.Equal("Failed to update rating (error 500). Try again.", detail.ActionError);
        // Not retryable: one attempt only.
        Assert.Single(Writes);
        Assert.Equal(0, _fixture.Changes.Version);
        Assert.Contains(_fixture.Logs.Lines, l => l.Contains("500", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(404, "Failed to update rating: this title is no longer available.")]
    [InlineData(503, "Failed to update rating. The server is busy, try again.")]
    public async Task ARefusedRating_SaysWhyAsTheTogglesDo(int status, string error)
    {
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.Problem(status, "refused"));
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(2);

        Assert.Equal(4, detail.UserRating);
        Assert.Equal(error, detail.ActionError);
    }

    [Fact]
    public async Task AFailedClear_PutsTheRatingBack()
    {
        RatedMovie(4).Silo.On(HttpMethod.Delete, Rating(MovieId), ScriptedSilo.NetworkError());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(4);

        Assert.Equal(4, detail.UserRating);
        Assert.Equal("Failed to update rating. Check the connection and try again.", detail.ActionError);
    }

    [Fact]
    public async Task TheRating_ChangesAtOnce()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held);
        var detail = await _fixture.LoadAsync(MovieId);

        var rating = detail.RateCommand.ExecuteAsync(5);

        Assert.Equal(5, detail.UserRating);
        Assert.True(detail.RateCommand.CanExecute(4));
        gate.SetResult(NoContentResponse());
        await rating;
        Assert.Equal(5, detail.UserRating);
    }

    [Fact]
    public async Task QuickClicks_EndOnTheLastStar()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(4), detail.RateCommand.ExecuteAsync(5)];
        Assert.Equal(5, detail.UserRating);
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(clicks);

        Assert.Equal(5, detail.UserRating);
        // One write at a time, and of the clicks made meanwhile only the last.
        Assert.Equal(["""{"rating":2}""", """{"rating":5}"""], Writes.Select(w => w.Body));
        Assert.False(detail.HasActionError);
    }

    [Fact]
    public async Task QuickClicksEndingOnAClear_DeleteTheRating()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held).On(HttpMethod.Delete, Rating(MovieId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(3), detail.RateCommand.ExecuteAsync(3)];
        Assert.Equal(0, detail.UserRating);
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(clicks);

        Assert.Equal(0, detail.UserRating);
        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], Writes.Select(w => w.Method));
    }

    [Fact]
    public async Task QuickClicksBackToTheSentStar_SendNothingMore()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held);
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(3), detail.RateCommand.ExecuteAsync(3), detail.RateCommand.ExecuteAsync(3)];
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(clicks);

        Assert.Equal(3, detail.UserRating);
        Assert.Single(Writes);
    }

    [Fact]
    public async Task AFailedWrite_WithANewerClickWaiting_EndsOnTheNewerClick()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(5)];
        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await Task.WhenAll(clicks);

        Assert.Equal(5, detail.UserRating);
        Assert.False(detail.HasActionError);
        Assert.Equal(["""{"rating":2}""", """{"rating":5}"""], Writes.Select(w => w.Body));
    }

    [Fact]
    public async Task AllFailingClicks_GoBackToWhatTheServerKept()
    {
        var (held, gate) = Held();
        RatedMovie(1).Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.Problem(503, "dependency_unavailable"));
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(5)];
        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await Task.WhenAll(clicks);

        Assert.Equal(1, detail.UserRating);
        Assert.Equal("Failed to update rating. The server is busy, try again.", detail.ActionError);
    }

    [Fact]
    public async Task AnUnexpectedFailure_PutsTheRatingBackAndFreesTheWrites()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(5)];
        gate.SetException(new InvalidOperationException("broken"));
        await Task.WhenAll(clicks);

        // The click waiting behind it is dropped, not left hanging, and the page shows what the server kept.
        Assert.Equal(4, detail.UserRating);
        Assert.False(detail.HasActionError);
        Assert.Single(Writes);
        Assert.Contains(_fixture.Logs.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));

        await detail.RateCommand.ExecuteAsync(3);

        Assert.Equal(3, detail.UserRating);
        Assert.Equal(["""{"rating":2}""", """{"rating":3}"""], Writes.Select(w => w.Body));
    }

    [Fact]
    public async Task AWriteForATitleLeft_DoesNotTouchTheNextTitle()
    {
        var (held, gate) = Held();
        _fixture.Movie().Series().Silo.On(HttpMethod.Put, Rating(MovieId), held);
        var detail = await _fixture.LoadAsync(MovieId);
        var rating = detail.RateCommand.ExecuteAsync(3);

        await detail.LoadCommand.ExecuteAsync(SeriesId);
        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await rating;

        Assert.Equal(0, detail.UserRating);
        Assert.False(detail.HasActionError);
    }

    [Fact]
    public async Task TheSameTitleOpenedAgainDuringAWrite_JoinsItAndEndsOnTheLastClick()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        var left = await _fixture.LoadAsync(MovieId);
        var first = left.RateCommand.ExecuteAsync(2);
        await Eventually(() => Writes.Count == 1);

        // Back and into the title again: the new page shows the click on its way, not the answer read meanwhile.
        var page = await _fixture.LoadAsync(MovieId);
        Assert.Equal(2, page.UserRating);
        var last = page.RateCommand.ExecuteAsync(5);
        // One writer for the title: nothing goes out beside the write on its way.
        Assert.Single(Writes);
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(first, last);

        Assert.Equal(5, page.UserRating);
        Assert.Equal(5, left.UserRating);
        Assert.Equal(["""{"rating":2}""", """{"rating":5}"""], Writes.Select(w => w.Body));
    }

    [Fact]
    public async Task TheJoinedPagesOwnClickRefused_GoesBackToTheWriteThatStuck()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.Problem(500, "internal"));
        var left = await _fixture.LoadAsync(MovieId);
        var first = left.RateCommand.ExecuteAsync(2);
        var page = await _fixture.LoadAsync(MovieId);
        var last = page.RateCommand.ExecuteAsync(5);

        gate.SetResult(NoContentResponse());
        await Task.WhenAll(first, last);

        // The 2 stuck, the 5 did not: the page shows the 2, and why the 5 failed.
        Assert.Equal(2, page.UserRating);
        Assert.Equal("Failed to update rating (error 500). Try again.", page.ActionError);
        Assert.Equal(["""{"rating":2}""", """{"rating":5}"""], Writes.Select(w => w.Body));
    }

    [Fact]
    public async Task AWriterIsGoneOnceItsWritesEnd_SoALaterPageShowsWhatItReads()
    {
        _fixture.Get(ItemPath(MovieId), Ok(Rated(Movie, 4)), Ok(Rated(Movie, 3))).Get(SimilarPath(MovieId), Ok(MovieSimilar))
            .Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.NoContent());
        var left = await _fixture.LoadAsync(MovieId);
        await left.RateCommand.ExecuteAsync(2);

        Assert.Null(_fixture.Ratings.InFlight(new Granary.Core.ViewModels.Detail.RatingTarget(ScriptedSilo.Base, "7", MovieId)));
        // Read after the write ended, its answer is newer than the write (changed elsewhere since): it shows as read.
        var page = await _fixture.LoadAsync(MovieId);
        Assert.Equal(3, page.UserRating);
    }

    [Fact]
    public async Task AReadAnsweredBeforeAWriteLanded_ShowsTheWriteInstead()
    {
        var (writeHeld, writeGate) = Held();
        var (readHeld, readGate) = Held();
        _fixture.Get(ItemPath(MovieId), Ok(Rated(Movie, 4)), Ok(Rated(Movie, 4)), readHeld).Get(SimilarPath(MovieId), Ok(MovieSimilar))
            .Silo.On(HttpMethod.Put, Rating(MovieId), writeHeld);
        var left = await _fixture.LoadAsync(MovieId);
        var page = await _fixture.LoadAsync(MovieId);
        var rating = left.RateCommand.ExecuteAsync(2);

        // The page is shown again (its load), reading while the write is on its way; the write ends before the answer shows.
        var load = page.LoadCommand.ExecuteAsync(MovieId);
        writeGate.SetResult(NoContentResponse());
        await rating;
        readGate.SetResult(await Ok(Rated(Movie, 4))());
        await load;

        Assert.Equal(2, page.UserRating);
    }

    [Fact]
    public async Task ARememberedEnd_IsForgottenAfterAMinute()
    {
        var (writeHeld, writeGate) = Held();
        var (readHeld, readGate) = Held();
        _fixture.Get(ItemPath(MovieId), Ok(Rated(Movie, 4)), readHeld).Get(SimilarPath(MovieId), Ok(MovieSimilar))
            .Silo.On(HttpMethod.Put, Rating(MovieId), writeHeld);
        var left = await _fixture.LoadAsync(MovieId);
        var rating = left.RateCommand.ExecuteAsync(2);
        var page = _fixture.Create();
        var load = page.LoadCommand.ExecuteAsync(MovieId);

        writeGate.SetResult(NoContentResponse());
        await rating;
        _fixture.Time.Advance(TimeSpan.FromSeconds(61));
        readGate.SetResult(await Ok(Rated(Movie, 4))());
        await load;

        Assert.Equal(4, page.UserRating);
    }

    [Fact]
    public async Task APageNotJoined_DoesNotSkipAClickItThinksIsStored()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.NoContent(), held, ScriptedSilo.NoContent());
        var other = await _fixture.LoadAsync(MovieId);
        var stale = await _fixture.LoadAsync(MovieId);
        await other.RateCommand.ExecuteAsync(2);

        // The stale page still shows 4. Its 1 fails, and its 4 waiting behind it must still be sent: the server keeps 2.
        Task[] clicks = [stale.RateCommand.ExecuteAsync(1), stale.RateCommand.ExecuteAsync(4)];
        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await Task.WhenAll(clicks);

        Assert.Equal(["""{"rating":2}""", """{"rating":1}""", """{"rating":4}"""], Writes.Select(w => w.Body));
        Assert.Equal(4, stale.UserRating);
        Assert.False(stale.HasActionError);
    }

    [Fact]
    public async Task ALoggerThatThrows_LeavesNoWriterHanging()
    {
        _fixture.Ratings = new(new Granary.Core.Api.Catalog.CatalogApi(_fixture.Silo.Http), _fixture.Time, new ThrowingLogger(), _fixture.Changes);
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.Problem(500, "internal"),
            () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("broken")), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);
        var ct = TestContext.Current.CancellationToken;

        await detail.RateCommand.ExecuteAsync(2).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(4, detail.UserRating);
        Assert.Equal("Failed to update rating (error 500). Try again.", detail.ActionError);
        await detail.RateCommand.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(4, detail.UserRating);

        // Each write ended, so the title's next click starts a writer of its own instead of waiting on a dead one.
        await detail.RateCommand.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(3, detail.UserRating);
    }

    [Fact]
    public async Task AFollowerOfChangesThatThrows_DoesNotDropTheWaitingClicks()
    {
        var (held, gate) = Held();
        _fixture.Movie().Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        _fixture.Changes.Changed += (_, _) => throw new InvalidOperationException("follower failed");
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(5)];
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(clicks);

        Assert.Equal(5, detail.UserRating);
        Assert.False(detail.HasActionError);
        Assert.Equal(["""{"rating":2}""", """{"rating":5}"""], Writes.Select(w => w.Body));
        Assert.Contains(_fixture.Logs.Lines, l => l.Contains("follower", StringComparison.OrdinalIgnoreCase) || l.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameTitleOpenedAgainDuringAWrite_GoesBackWhenTheWriteFails()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), held);
        var left = await _fixture.LoadAsync(MovieId);
        var first = left.RateCommand.ExecuteAsync(2);
        var page = await _fixture.LoadAsync(MovieId);
        Assert.Equal(2, page.UserRating);

        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await first;

        await Eventually(() => page.UserRating == 4);
        Assert.Equal("Failed to update rating (error 500). Try again.", page.ActionError);
    }

    [Fact]
    public async Task AProfileChangedDuringTheLoad_KeepsTheWritesWithTheProfileTheTitleWasReadFor()
    {
        var (held, gate) = Held();
        _fixture.Get(ItemPath(MovieId), held).Get(SimilarPath(MovieId), Ok(MovieSimilar))
            .Silo.On(HttpMethod.Put, Rating(MovieId), ScriptedSilo.NoContent());
        var detail = _fixture.Create();
        var load = detail.LoadCommand.ExecuteAsync(MovieId);
        await Eventually(() => _fixture.Silo.Sent.Any(r => r.Uri.AbsolutePath.EndsWith(ItemPath(MovieId), StringComparison.Ordinal)));

        _fixture.Session.ProfileId = "8";
        gate.SetResult(await Ok(Rated(Movie, 4))());
        await load;
        await detail.RateCommand.ExecuteAsync(2);

        // The rating shown is profile 7's, so its writes are too.
        Assert.Equal("7", Assert.Single(Writes).AsProfile);
    }

    [Fact]
    public async Task ClicksAfterAProfileSwitch_AreNeverSent()
    {
        var (held, gate) = Held();
        RatedMovie(4).Silo.On(HttpMethod.Put, Rating(MovieId), held, ScriptedSilo.NoContent());
        _fixture.Silo.ActingProfile = () => _fixture.Session.ProfileId;
        var detail = await _fixture.LoadAsync(MovieId);

        Task[] clicks = [detail.RateCommand.ExecuteAsync(2), detail.RateCommand.ExecuteAsync(5)];
        await Eventually(() => Writes.Count == 1);
        _fixture.Session.ProfileId = "8";
        gate.SetResult(NoContentResponse());
        await Task.WhenAll(clicks);

        // The 2 went out before the switch and was kept; the 5 waiting behind it is refused unsent, without an error.
        Assert.Equal(["""{"rating":2}"""], Writes.Select(w => w.Body));
        Assert.Equal(2, detail.UserRating);
        Assert.False(detail.HasActionError);
    }

    [Fact]
    public async Task ASeries_TakesStars()
    {
        _fixture.Series().Silo.On(HttpMethod.Put, Rating(SeriesId), ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(SeriesId);

        await detail.RateCommand.ExecuteAsync(5);

        Assert.True(detail.CanRate);
        Assert.Equal(5, detail.UserRating);
        Assert.Equal("/silo/api/v2/ratings/series-tmdb-96677", Assert.Single(Writes).Uri.AbsolutePath);
    }

    [Fact]
    public async Task SeasonsAndEpisodes_HaveNoStars()
    {
        var season = await _fixture.Season().LoadAsync(Season3Id);
        var episode = await _fixture.Episode().LoadAsync(EpisodeId);

        await season.RateCommand.ExecuteAsync(3);
        await episode.RateCommand.ExecuteAsync(3);

        Assert.False(season.CanRate);
        Assert.False(episode.CanRate);
        Assert.Equal(0, season.UserRating);
        Assert.Equal(0, episode.UserRating);
        Assert.Empty(Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task StarsOutsideOneToFive_AreIgnored(int stars)
    {
        var detail = await RatedMovie(4).LoadAsync(MovieId);

        await detail.RateCommand.ExecuteAsync(stars);

        Assert.Equal(4, detail.UserRating);
        Assert.Empty(Writes);
    }

    // A logger whose every line fails, as a full disk or a broken sink would.
    private sealed class ThrowingLogger : ILogger<Granary.Core.ViewModels.Detail.RatingWrites>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("The log failed.");
    }
}
