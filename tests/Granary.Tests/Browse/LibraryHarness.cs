using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>
/// A signed-in profile on a scripted server with three libraries: the routes a library page asks answer with
/// <see cref="LibraryPayloads"/>; tests re-script the ones they need otherwise.
/// </summary>
internal sealed class LibraryHarness
{
    /// <summary>The image parameters of <see cref="ImageSizes.Default"/> on a server that takes every slot.</summary>
    public const string ImageQuery = "poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185";

    public const string CatalogPath = "api/v2/catalog";

    public LibraryHarness()
    {
        Session.State = SessionState.Ready;
        Session.ActiveServer = ScriptedSilo.Server;
        Session.ProfileId = "7";
        Session.Account = new Account("1", "elena", "", "user", [], true, false, null);
        Answer("api/v2/images/capabilities", CatalogPayloads.ImageCapabilities);
        Answer("api/v2/user/libraries", LibraryPayloads.Libraries);
        Answer("api/v2/shuffles/capabilities", LibraryPayloads.ShuffleAvailable);
        Answer("api/v2/library/1/sections", LibraryPayloads.Sections);
        Answer("api/v2/library/1/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        Answer("api/v2/library/1/collections", LibraryPayloads.Collections);
        Answer("api/v2/catalog/filters", LibraryPayloads.Filters);
        Answer(CatalogPath, LibraryPayloads.FirstPage, LibraryPayloads.LastPage);
    }

    public ScriptedSilo Silo { get; } = new();

    public FakeSessionManager Session { get; } = new();

    public RecordingNavigation Navigation { get; } = new();

    public LogSink Logs { get; } = new();

    public ManualTimeProvider Clock { get; } = new();

    /// <summary>Answers GET <paramref name="path"/> with the bodies in order, repeating the last.</summary>
    public void Answer(string path, params string[] bodies) =>
        Silo.On(HttpMethod.Get, path, [.. bodies.Select(body => ScriptedSilo.Json(HttpStatusCode.OK, body))]);

    public void Answer(string path, params Func<Task<HttpResponseMessage>>[] responders) => Silo.On(HttpMethod.Get, path, responders);

    public LibraryViewModel Create() => new(
        Session,
        new CatalogApi(Silo.Http),
        new LibraryPageApi(Silo.Http),
        new ImageParameterCache(new ImagesApi(Silo.Http), Logs.For<ImageParameterCache>()),
        Navigation,
        new ImmediateDispatcher(),
        Clock,
        Logs.For<LibraryViewModel>());

    /// <summary>Library 1, shown on its Library tab with the first page loaded.</summary>
    public async Task<LibraryViewModel> OpenLibraryTabAsync(string libraryId = "1")
    {
        var library = Create();
        await library.ActivateAsync(libraryId);
        await library.SelectTabCommand.ExecuteAsync(LibraryTab.Library);
        return library;
    }

    /// <summary>The query strings sent to GET <paramref name="path"/>, oldest first, without the "?".</summary>
    public List<string> Queries(string path) => [.. Silo.SentTo(HttpMethod.Get, path).Select(r => r.Uri.Query.TrimStart('?'))];

    public int Count(string path) => Silo.SentTo(HttpMethod.Get, path).Count;

    /// <summary>A response that waits for <paramref name="release"/>, for requests caught in flight.</summary>
    public static Func<Task<HttpResponseMessage>> Held(TaskCompletionSource release, string body) => async () =>
    {
        await release.Task;
        return await ScriptedSilo.Json(HttpStatusCode.OK, body)();
    };
}
