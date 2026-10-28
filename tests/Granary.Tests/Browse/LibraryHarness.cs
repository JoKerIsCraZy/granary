using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
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

    public const string LettersPath = "api/v2/catalog/letters";

    public const string SettingsPath = "api/v2/settings/values/effective";

    /// <summary>The profile hides no library and keeps the server's order.</summary>
    public const string NoLibraryPreferences =
        """{ "items": [{ "key": "ui.disabled_library_ids", "value": null }, { "key": "ui.library_order", "value": null }], "revision": 1 }""";

    public LibraryHarness()
    {
        Session.State = SessionState.Ready;
        Session.ActiveServer = ScriptedSilo.Server;
        Session.ProfileId = "7";
        Session.Account = new Account("1", "elena", "", "user", [], true, false, null);
        Answer("api/v2/images/capabilities", CatalogPayloads.ImageCapabilities);
        Answer("api/v2/user/libraries", LibraryPayloads.Libraries);
        Answer(SettingsPath, NoLibraryPreferences);
        Answer("api/v2/shuffles/capabilities", LibraryPayloads.ShuffleAvailable);
        Answer("api/v2/library/1/sections", LibraryPayloads.Sections);
        Answer("api/v2/library/1/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        Answer("api/v2/library/1/collections", LibraryPayloads.Collections);
        Answer("api/v2/catalog/filters", LibraryPayloads.Filters);
        Answer(CatalogPath, LibraryPayloads.OnlyPage);
        AnswerLetters(ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.Letters));
    }

    public ScriptedSilo Silo { get; } = new();

    public FakeSessionManager Session { get; } = new();

    public RecordingNavigation Navigation { get; } = new();

    public LogSink Logs { get; } = new();

    public ManualTimeProvider Clock { get; } = new();

    public ViewerStateChanges ViewerState { get; } = new(new PlaybackStops());

    /// <summary>Where the libraries' last tabs are kept; none unless a test sets it.</summary>
    public LibraryTabs? Tabs { get; set; }

    /// <summary>Answers GET <paramref name="path"/> with the bodies in order, repeating the last.</summary>
    public void Answer(string path, params string[] bodies) =>
        Silo.On(HttpMethod.Get, path, [.. bodies.Select(body => ScriptedSilo.Json(HttpStatusCode.OK, body))]);

    public void Answer(string path, params Func<Task<HttpResponseMessage>>[] responders) => Silo.On(HttpMethod.Get, path, responders);

    /// <summary>Answers the letter index (a POST) in order, repeating the last.</summary>
    public void AnswerLetters(params Func<Task<HttpResponseMessage>>[] responders) => Silo.On(HttpMethod.Post, LettersPath, responders);

    /// <summary>The bodies sent to the letter index, oldest first.</summary>
    public List<string> LetterBodies() => [.. Silo.SentTo(HttpMethod.Post, LettersPath).Select(r => r.Body ?? "")];

    /// <summary>The libraries the profile browses, read through <see cref="Silo"/>.</summary>
    public VisibleLibraries Libraries() => new(new CatalogApi(Silo.Http), new SettingsApi(Silo.Http), Logs.For<VisibleLibraries>());

    public LibraryViewModel Create() => new(
        Session,
        new CatalogApi(Silo.Http),
        Libraries(),
        new LibraryPageApi(Silo.Http),
        new ImageParameterCache(new ImagesApi(Silo.Http), Logs.For<ImageParameterCache>()),
        Navigation,
        new ImmediateDispatcher(),
        Clock,
        Logs.For<LibraryViewModel>(),
        ViewerState,
        Tabs);

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
