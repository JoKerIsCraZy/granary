using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.Browse;

/// <summary>
/// A signed-in profile on a <see cref="ScriptedSilo"/> with image capabilities, and the detail view model on
/// top of it. Each test scripts the item calls it needs; an unscripted call fails its section.
/// </summary>
internal sealed class ItemDetailFixture
{
    public ItemDetailFixture()
    {
        Changes = new(Stops);
        Silo.On(HttpMethod.Get, "api/v2/images/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities));
    }

    public ScriptedSilo Silo { get; } = new();

    public FakeSessionManager Session { get; } = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "7",
    };

    public RecordingNavigation Navigation { get; } = new();

    public LogSink Logs { get; } = new();

    public RecordingArtworkPrefetcher Artwork { get; } = new();

    /// <summary>The stops of the app's playbacks: one that ended is a change of the viewer's state (<see cref="Changes"/>).</summary>
    public PlaybackStops Stops { get; } = new();

    /// <summary>The app's count of the viewer's changes, which a write the server kept adds to.</summary>
    public ViewerStateChanges Changes { get; }

    public static string ItemPath(string id) => $"api/v2/catalog/items/{id}";

    public static string SeasonsPath(string seriesId) => $"api/v2/catalog/series/{seriesId}/seasons";

    public static string SeasonItemEpisodesPath(string seasonId) => $"api/v2/catalog/items/{seasonId}/episodes";

    public static string SeriesEpisodesPath(string seriesId, int season) => $"api/v2/catalog/series/{seriesId}/seasons/{season}/episodes";

    public static string SimilarPath(string id) => $"api/v2/recommendations/similar/{id}";

    public static Func<Task<HttpResponseMessage>> Ok(string json) => ScriptedSilo.Json(HttpStatusCode.OK, json);

    public static Func<Task<HttpResponseMessage>> Busy() => ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable);

    public ItemDetailFixture Get(string path, params Func<Task<HttpResponseMessage>>[] responders)
    {
        Silo.On(HttpMethod.Get, path, responders);
        return this;
    }

    public ItemDetailFixture Movie() => Get(ItemPath(ItemDetailPayloads.MovieId), Ok(ItemDetailPayloads.Movie))
        .Get(SimilarPath(ItemDetailPayloads.MovieId), Ok(ItemDetailPayloads.MovieSimilar));

    public ItemDetailFixture Series(string seasons = ItemDetailPayloads.SeriesSeasons) =>
        Get(ItemPath(ItemDetailPayloads.SeriesId), Ok(ItemDetailPayloads.Series))
        .Get(SeasonsPath(ItemDetailPayloads.SeriesId), Ok(seasons))
        .Get(SimilarPath(ItemDetailPayloads.SeriesId), Ok(ItemDetailPayloads.SeriesSimilar))
        .EpisodeLists();

    /// <summary>The episode lists of <see cref="ItemDetailPayloads.SeriesSeasons"/>, which the series page prefetches.</summary>
    public ItemDetailFixture EpisodeLists() =>
        Get(SeasonItemEpisodesPath("season-96677-0"), Ok(ItemDetailPayloads.NoEpisodes))
        .Get(SeasonItemEpisodesPath("season-96677-1"), Ok(ItemDetailPayloads.SeasonOneEpisodes))
        .Get(SeasonItemEpisodesPath("season-96677-2"), Ok(ItemDetailPayloads.NoEpisodes))
        .Get(SeasonItemEpisodesPath(ItemDetailPayloads.Season3Id), Ok(ItemDetailPayloads.SeasonEpisodes));

    public ItemDetailFixture Season() => Get(ItemPath(ItemDetailPayloads.Season3Id), Ok(ItemDetailPayloads.Season))
        .Get(SeasonItemEpisodesPath(ItemDetailPayloads.Season3Id), Ok(ItemDetailPayloads.SeasonEpisodes));

    public ItemDetailFixture Episode() => Get(ItemPath(ItemDetailPayloads.EpisodeId), Ok(ItemDetailPayloads.Episode))
        .Get(SeasonsPath(ItemDetailPayloads.SeriesId), Ok(ItemDetailPayloads.SeriesSeasons))
        .Get(SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 3), Ok(ItemDetailPayloads.SeasonEpisodes));

    /// <summary>The app's queue of track choices on their way to the server, which a start waits for.</summary>
    public TrackChoiceWrites TrackChoices => field ??= new(Session, TimeProvider.System, NullLogger<TrackChoiceWrites>.Instance);

    /// <summary>The page's UI thread; work runs at once unless a test sets another.</summary>
    public Core.Abstractions.IUiDispatcher Dispatcher { get; set; } = new ImmediateDispatcher();

    /// <summary>Drives the wait before the page reads changes of the viewer's state.</summary>
    public ManualTimeProvider Clock { get; } = new();

    /// <summary>The profile's spoiler switches; none unless a test sets them, so nothing is hidden.</summary>
    public EpisodeSpoilerSettings? Spoilers { get; set; }

    /// <summary>The profile's other Title Pages settings; none unless a test sets them, so every one is at its default.</summary>
    public TitlePageSettings? TitlePage { get; set; }

    /// <summary>The app's theme music; none unless a test sets it, so the page plays nothing.</summary>
    public Core.Playback.Themes.ThemeMusic? ThemeMusic { get; set; }

    /// <summary>The calendar's airings a season's grid places its missing and upcoming episodes by; none unless a test sets them.</summary>
    public SeriesAirings? Airings { get; set; }

    /// <summary>The reads of a listed episode's multi-episode file the places are checked with; none unless a test sets them.</summary>
    public EpisodeSpans? Spans { get; set; }

    /// <summary>The box set reads a movie's row comes from; none unless a test sets them, so the row stays out.</summary>
    public BoxSetsApi? BoxSets { get; set; }

    /// <summary>Whether a library has box sets on, which a movie's page asks before its box set; none unless a test sets it.</summary>
    public BoxSetLibraries? BoxSetLibraries { get; set; }

    /// <summary>The clock of the ratings' remembered ends.</summary>
    public ManualTimeProvider Time { get; } = new();

    /// <summary>The app's star ratings on their way to the server, shared by every page the fixture makes.</summary>
    public RatingWrites Ratings
    {
        get => field ??= new(new CatalogApi(Silo.Http), Time, Logs.For<RatingWrites>(), Changes);
        set;
    }

    public ItemDetailViewModel Create() => new(
        Session,
        new CatalogApi(Silo.Http),
        new ImageParameterCache(new ImagesApi(Silo.Http), Logs.For<ImageParameterCache>()),
        Navigation,
        Artwork,
        Logs.For<ItemDetailViewModel>(),
        changes: Changes,
        trackPreferences: new TrackPreferencesApi(Silo.Http),
        trackChoices: TrackChoices,
        spoilers: Spoilers,
        titlePage: TitlePage,
        themeMusic: ThemeMusic,
        dispatcher: Dispatcher,
        time: Clock,
        ratings: Ratings,
        airings: Airings,
        spans: Spans,
        boxSets: BoxSets,
        boxSetLibraries: BoxSetLibraries);

    /// <summary>The page once loaded, the places of a season's missing and upcoming episodes included, which come after.</summary>
    public async Task<ItemDetailViewModel> LoadAsync(string id)
    {
        var viewModel = Create();
        await viewModel.LoadCommand.ExecuteAsync(id);
        await viewModel.Placing;
        return viewModel;
    }

    /// <summary>A response the test releases when it wants, to look at the page while the call is pending.</summary>
    public static (Func<Task<HttpResponseMessage>> Responder, TaskCompletionSource<HttpResponseMessage> Gate) Held()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (() => gate.Task, gate);
    }

    public static HttpResponseMessage NoContentResponse() => new(HttpStatusCode.NoContent);
}
