using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The title page's Download items in its ⋯ menu and its episode cards' entries: Download opens the dialog, a download on
/// this PC shows its status and opens the Downloads page, a downloaded one can be deleted after asking, and a season page
/// offers "Download season".
/// </summary>
public sealed class ItemDetailDownloadsTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";
    private const string MovieId = "mv_1";
    private const string SeasonId = "se_1";
    private const string EpisodeId = "ep_5";

    private readonly DownloadHarness _h = new();
    private readonly ItemDetailFixture _detail;
    private readonly FakeDownloadOptionsDialog _dialog = new();
    private readonly FakeConfirmation _confirmation = new();

    public ItemDetailDownloadsTests()
    {
        _detail = new ItemDetailFixture(_h.Silo, _h.Session);
        _detail.Get(ItemDetailFixture.ItemPath(MovieId), ItemDetailFixture.Ok(TitlePayloads.Movie))
            .Get(ItemDetailFixture.SimilarPath(MovieId), ItemDetailFixture.Ok("""{ "items": [] }"""))
            .Get(ItemDetailFixture.ItemPath(SeasonId), ItemDetailFixture.Ok(TitlePayloads.Season))
            .Get(ItemDetailFixture.SeasonItemEpisodesPath(SeasonId), ItemDetailFixture.Ok(TitlePayloads.SeasonEpisodes))
            .Get(ItemDetailFixture.ItemPath(EpisodeId), ItemDetailFixture.Ok(TitlePayloads.Episode))
            .Get(ItemDetailFixture.SeasonsPath("sr_1"), ItemDetailFixture.Ok("""{ "items": [] }"""))
            .Get(ItemDetailFixture.SeriesEpisodesPath("sr_1", 1), ItemDetailFixture.Ok(TitlePayloads.SeasonEpisodes));
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadsFeature Feature { get; set; } = null!;

    private DownloadManager Manager { get; set; } = null!;

    // The page's Download items over the harness, on a server that offers what the test scripted (Original by default).
    private ItemDetailViewModel Page(DownloadQueue? queue = null, IUiDispatcher? ui = null)
    {
        Feature = _h.NewFeature();
        Manager = _h.NewManager(queue, Feature);
        var dispatcher = ui ?? new ImmediateDispatcher();
        _detail.Downloads = new DownloadActions(Manager, Feature, _dialog, _confirmation, _detail.Navigation, dispatcher,
            () => new DownloadOptionsViewModel(Manager, Feature, _h.Preferences, _h.Disk, dispatcher), _h.Logs.For<DownloadActions>());
        return _detail.Create();
    }

    // The page loaded and following the downloads, once it has read what the server offers.
    private async Task<ItemDetailViewModel> FollowedAsync(string id, DownloadQueue? queue = null, IUiDispatcher? ui = null)
    {
        var page = Page(queue, ui);
        await page.LoadCommand.ExecuteAsync(id);
        page.FollowDownloads();
        await Until(() => Feature.Capability is not null, "the capability was read");
        return page;
    }

    private void DownloadsOff() =>
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: true, allowed: false)));

    private void Creates(params Func<Task<HttpResponseMessage>>[] answers) => _h.Silo.On(HttpMethod.Post, CreatePath, answers);

    private static Func<Task<HttpResponseMessage>> Created(string entry) =>
        ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, entry));

    private List<JsonElement> CreateBodies() =>
        [.. _h.Silo.SentTo(HttpMethod.Post, CreatePath).Select(r => JsonDocument.Parse(r.Body!).RootElement)];

    [Fact]
    public async Task AMovie_OffersDownload_WhenTheServerAllows()
    {
        var page = await FollowedAsync(MovieId);

        await Until(() => page.ShowsDownload, "Download showed");
        Assert.Equal(("Download", false, true), (page.DownloadLabel, page.CanDeleteDownload, page.ShowsMoreMenu));

        await page.OpenDownloadCommand.ExecuteAsync(null);

        var shown = Assert.Single(_dialog.Shown);
        await shown.SpaceRead;
        Assert.Equal(("Download: Example Movie", "4567", "8.0 GB"), (shown.Title, shown.SelectedVersion?.FileId, shown.SizeText));
    }

    [Fact]
    public async Task AMovie_OffersNoDownload_WhenTheServerDoesNotAllowIt()
    {
        DownloadsOff();

        var page = await FollowedAsync(MovieId);

        Assert.False(page.ShowsDownload);
        Assert.False(page.CanDeleteDownload);
        // The menu still holds the watchlist and collections.
        Assert.True(page.ShowsMoreMenu);
    }

    [Fact]
    public async Task WhileDownloading_TheItemShowsTheStatus_AndOpensDownloads()
    {
        await _h.AddAsync("dl_1", mediaItemId: MovieId);
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var queue = _h.StartQueue();
        var page = await FollowedAsync(MovieId, queue);

        body.Release(1260);

        await Until(() => page.DownloadLabel == "Downloading · 42%", "the item showed the progress");
        Assert.True(page.ShowsDownload);
        await page.OpenDownloadCommand.ExecuteAsync(null);
        Assert.Equal([AppRoute.Downloads], _detail.Navigation.Sections);
        Assert.Empty(_dialog.Shown);
    }

    [Fact]
    public async Task Downloaded_OffersDelete_AndAsksFirst()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: MovieId);
        var page = await FollowedAsync(MovieId);
        await Until(() => page.DownloadLabel == "Downloaded", "the item said it is downloaded");
        Assert.True(page.CanDeleteDownload);

        _confirmation.Answer = false;
        await page.DeleteDownloadCommand.ExecuteAsync(null);
        Assert.NotNull(_h.Find("dl_1"));

        _confirmation.Answer = true;
        await page.DeleteDownloadCommand.ExecuteAsync(null);

        Assert.Equal(("Delete download?", "This removes the downloaded copy from this PC. Nothing is deleted from your Silo server.", "Delete"),
            _confirmation.Asked[0]);
        Assert.Null(_h.Find("dl_1"));
        await Until(() => page.DownloadLabel == "Download" && !page.CanDeleteDownload, "the item offered Download again");
    }

    [Fact]
    public async Task ADownloadedTitle_StaysDeletable_WhenTheServerStoppedDownloads()
    {
        DownloadsOff();
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: MovieId);

        var page = await FollowedAsync(MovieId);

        await Until(() => page.DownloadLabel == "Downloaded", "the item said it is downloaded");
        Assert.True(page.ShowsDownload);
        Assert.True(page.CanDeleteDownload);
    }

    [Fact]
    public async Task AnEpisodePage_DownloadsTheEpisodeOfItsSeries()
    {
        Creates(Created(DownloadPayloads.Entry("dl_5", "ready", episodeId: EpisodeId)));
        _dialog.WhileShown = options => options.StartAsync();
        var page = await FollowedAsync(EpisodeId);
        await Until(() => page.ShowsDownload, "Download showed");
        // An episode has no favourite or watchlist: the menu is there for Download.
        Assert.True(page.ShowsMoreMenu);

        await page.OpenDownloadCommand.ExecuteAsync(null);

        Assert.Equal("Download: Example Series · S1 · E5", Assert.Single(_dialog.Shown).Title);
        var body = Assert.Single(CreateBodies());
        Assert.Equal(("sr_1", EpisodeId, "4575"), (body.GetProperty("content_id").GetString(), body.GetProperty("episode_id").GetString(),
            body.GetProperty("media_file_id").GetString()));
        var record = _h.Find("dl_5")!.Record;
        Assert.Equal(("Chapter Five", "Example Series", 1, 5), (record.Title, record.SeriesTitle, record.SeasonNumber, record.EpisodeNumber));
    }

    [Fact]
    public async Task ASeason_OffersDownloadSeason()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")), Created(DownloadPayloads.Entry("dl_5", "ready", episodeId: EpisodeId)));
        _dialog.WhileShown = options => options.StartAsync();
        var page = await FollowedAsync(SeasonId);

        await Until(() => page.ShowsDownload, "Download season showed");
        Assert.Equal(("Download season", false, true), (page.DownloadLabel, page.CanDeleteDownload, page.ShowsMoreMenu));

        await page.OpenDownloadCommand.ExecuteAsync(null);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("Download: Example Series · Season 1", "2 episodes · 3.0 GB"), (shown.Title, shown.SizeText));
        var bodies = CreateBodies();
        Assert.Equal(["ep_1", EpisodeId], bodies.Select(b => b.GetProperty("episode_id").GetString()));
        Assert.All(bodies, b => Assert.Equal("sr_1", b.GetProperty("content_id").GetString()));
        Assert.Equal("Chapter One", _h.Find("dl_1")!.Record.Title);
    }

    [Fact]
    public async Task TheSeasonsEpisodeCards_GetDownloadEntries()
    {
        var page = await FollowedAsync(SeasonId);
        await Until(() => page.ShowsDownload, "Download season showed");
        var first = page.Episodes[0].Card;
        var fifth = page.Episodes[1].Card;

        Assert.Equal(["Download"], page.EntriesFor(first).Select(e => e.Label));
        Assert.Empty(page.EntriesFor(new MediaCard(MovieId, CatalogItemTypes.Movie, "Example Movie")));

        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: EpisodeId,
            change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series", SeasonNumber = 1, EpisodeNumber = 5 });
        var entries = page.EntriesFor(fifth);

        Assert.Equal(["Downloaded", "Delete download"], entries.Select(e => e.Label));
        entries[0].Command.Execute(fifth);
        Assert.Equal([AppRoute.Downloads], _detail.Navigation.Sections);
        await RunAsync(entries[1].Command, fifth);
        Assert.Null(_h.Find("dl_5"));
        Assert.Single(_confirmation.Asked);
    }

    [Fact]
    public async Task AnEpisodeCardsDownload_OffersItsFiles_TheFirstOneChosen()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")));
        _dialog.WhileShown = options => options.StartAsync();
        var page = await FollowedAsync(SeasonId);
        await Until(() => page.ShowsDownload, "Download season showed");
        var first = page.Episodes[0].Card;

        await RunAsync(Assert.Single(page.EntriesFor(first)).Command, first);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal("Download: Example Series · S1 · E1", shown.Title);
        Assert.Equal([("4571", "1080p · H264"), ("4570", "720p · H264")], shown.Versions.Select(v => (v.FileId, v.Summary)));
        Assert.Equal("4570", shown.SelectedVersion?.FileId);
        var body = Assert.Single(CreateBodies());
        Assert.Equal(("sr_1", "ep_1", "4570"), (body.GetProperty("content_id").GetString(), body.GetProperty("episode_id").GetString(),
            body.GetProperty("media_file_id").GetString()));
    }

    [Fact]
    public async Task ProgressOfAnotherDownload_LeavesThePage_ItsOwnRefreshesItOnce()
    {
        var (other, own, page) = await ProgressPostsAsync(MovieId, own: MovieId, other: "mv_2");

        Assert.Equal((0, 1), (other, own));
        Assert.Equal("Downloading · 20%", page.DownloadLabel);
    }

    [Fact]
    public async Task ASeasonPage_FollowsTheProgressOfItsEpisodes()
    {
        var (other, own, _) = await ProgressPostsAsync(SeasonId, own: EpisodeId, other: MovieId);

        Assert.Equal((0, 1), (other, own));
    }

    [Fact]
    public async Task StoppingToFollow_LeavesTheItemsAsTheyWere()
    {
        var page = await FollowedAsync(MovieId);
        await Until(() => page.ShowsDownload, "Download showed");

        page.StopFollowingDownloads();
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: MovieId);

        Assert.Equal("Download", page.DownloadLabel);
        page.FollowDownloads();
        await Until(() => page.DownloadLabel == "Downloaded", "following again showed the download");
    }

    // The page's own download and another one run side by side. Once both run quietly, the other one goes further, then the
    // page's: how many refreshes each posted to the UI thread, and the page after them.
    private async Task<(int Other, int Own, ItemDetailViewModel Page)> ProgressPostsAsync(string pageId, string own, string other)
    {
        await _h.AddAsync("dl_1", mediaItemId: own);
        await _h.AddAsync("dl_2", mediaItemId: other);
        var ownBody = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var otherBody = _h.ServeGated("dl_2", DownloadPayloads.Source(3000));
        var queue = _h.StartQueue();
        var ui = new QueuedDispatcher();
        var page = await FollowedAsync(pageId, queue, ui);
        ownBody.Release(300);
        otherBody.Release(300);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 300 && queue.BytesDone(Scope7, "dl_2") == 300, "both transfers ran");
        await SettleRunningAsync(queue);
        ui.RunAll();
        var changes = new ConcurrentQueue<EventArgs>();
        Manager.Changed += (_, e) => changes.Enqueue(e);
        int before = ui.Posts;
        // Half a second on, each transfer's progress is told again.
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        otherBody.Release(300);
        await Until(() => Progressed(changes, "dl_2"), "the other download went further");
        int otherPosts = ui.Posts - before;
        ownBody.Release(300);
        await Until(() => Progressed(changes, "dl_1"), "the page's download went further");
        int ownPosts = ui.Posts - before - otherPosts;

        // Nothing but progress came meanwhile, so the posts are the progress's.
        Assert.All(changes, e => Assert.IsType<DownloadProgressedEventArgs>(e));
        ui.RunAll();
        return (otherPosts, ownPosts, page);
    }

    private static bool Progressed(IEnumerable<EventArgs> changes, string downloadId) =>
        changes.Any(e => e is DownloadProgressedEventArgs progressed && progressed.DownloadId == downloadId);

    // A card entry's command as the card's menu runs it, awaited when it runs asynchronously (Download, Delete).
    private static Task RunAsync(ICommand command, object? parameter)
    {
        if (command is IAsyncRelayCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync(parameter);
        }
        command.Execute(parameter);
        return Task.CompletedTask;
    }

    // A UI thread the test runs by hand: posted work waits until RunAll, and every post is counted.
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _work = new();
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public bool HasThreadAccess => true;

        public void Post(Action action)
        {
            Interlocked.Increment(ref _posts);
            _work.Enqueue(action);
        }

        public void RunAll()
        {
            while (_work.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    /// <summary>A film, a season of "Example Series" with two episodes, and its fifth episode, as the catalog answers them.</summary>
    private static class TitlePayloads
    {
        public const string Movie = """
            {
              "content_id": "mv_1", "type": "movie", "title": "Example Movie", "status": "matched", "genres": [], "keywords": [],
              "year": 2026, "runtime": 120,
              "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
              "versions": [
                { "file_id": "4567", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv", "hdr": false,
                  "file_size": 8589934592, "duration": 7200, "bitrate": 9000 }
              ],
              "videos": [], "subtitles": []
            }
            """;

        public const string Season = """
            {
              "content_id": "se_1", "type": "season", "title": "Season 1", "status": "matched", "genres": [], "keywords": [],
              "series_id": "sr_1", "series_title": "Example Series", "season_number": 1, "episode_count": 2,
              "versions": [], "subtitles": []
            }
            """;

        public const string SeasonEpisodes = """
            {
              "items": [
                { "content_id": "ep_1", "season_number": 1, "episode_number": 1, "title": "Chapter One", "runtime": 50,
                  "files": [
                    { "file_id": "4570", "resolution": "720p", "codec_video": "h264", "hdr": false, "file_size": 1073741824 },
                    { "file_id": "4571", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 2147483648 },
                    { "file_id": "4572", "resolution": "1080p", "hdr": false, "file_size": 4294967296, "unreadable": true }
                  ] },
                { "content_id": "ep_5", "season_number": 1, "episode_number": 5, "title": "Chapter Five", "runtime": 50,
                  "files": [{ "file_id": "4575", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 2147483648 }] },
                { "content_id": "ep_6", "season_number": 1, "episode_number": 6, "title": "Chapter Six", "runtime": 50, "files": [] }
              ]
            }
            """;

        public const string Episode = """
            {
              "content_id": "ep_5", "type": "episode", "title": "Chapter Five", "status": "matched", "genres": [], "keywords": [],
              "series_id": "sr_1", "series_title": "Example Series", "season_number": 1, "episode_number": 5, "runtime": 50,
              "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
              "versions": [
                { "file_id": "4575", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv", "hdr": false,
                  "file_size": 2147483648, "duration": 3000, "bitrate": 5000 }
              ],
              "videos": [], "subtitles": []
            }
            """;
    }
}
