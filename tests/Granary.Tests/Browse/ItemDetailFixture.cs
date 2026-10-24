using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>
/// A signed-in profile on a <see cref="ScriptedSilo"/> with image capabilities, and the detail view model on
/// top of it. Each test scripts the item calls it needs; an unscripted call fails its section.
/// </summary>
internal sealed class ItemDetailFixture
{
    public ItemDetailFixture()
    {
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

    public ItemDetailViewModel Create() => new(
        Session,
        new CatalogApi(Silo.Http),
        new ImageParameterCache(new ImagesApi(Silo.Http), Logs.For<ImageParameterCache>()),
        Navigation,
        Artwork,
        Logs.For<ItemDetailViewModel>());

    public async Task<ItemDetailViewModel> LoadAsync(string id)
    {
        var viewModel = Create();
        await viewModel.LoadCommand.ExecuteAsync(id);
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
