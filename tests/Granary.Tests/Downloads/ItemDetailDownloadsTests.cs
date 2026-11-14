using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Granary.Core.Abstractions;
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
/// The title page's Download items in its ⋯ menu: Download opens the dialog, a download on this PC shows its status and opens
/// the Downloads page, a downloaded one can be deleted after asking, and a season page offers "Download season".
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
            () => new DownloadOptionsViewModel(Manager, Feature, _h.Preferences, _h.Disk, dispatcher, _h.Time, _h.Logs.For<DownloadOptionsViewModel>()),
            _h.Logs.For<DownloadActions>());
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
        // The files the size counted are the ones asked for: each episode's first file (1 GB and 2 GB).
        Assert.Equal(["4570", "4575"], bodies.Select(b => b.GetProperty("media_file_id").GetString()));
        Assert.Equal("Chapter One", _h.Find("dl_1")!.Record.Title);
    }

    [Fact]
    public async Task ASeason_LeavesOutTheEpisodesOnThisPC_AndGoesOnceAllAre()
    {
        Creates(Created(DownloadPayloads.Entry("dl_5", "ready", episodeId: EpisodeId)));
        _dialog.WhileShown = options => options.StartAsync();
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1");
        var page = await FollowedAsync(SeasonId);
        await Until(() => page.ShowsDownload, "Download season showed");

        await page.OpenDownloadCommand.ExecuteAsync(null);

        // Only the fifth episode starts: the dialog says so, with its file's size.
        Assert.Equal("1 episode · 2.0 GB", Assert.Single(_dialog.Shown).SizeText);
        Assert.Equal([EpisodeId], CreateBodies().Select(b => b.GetProperty("episode_id").GetString()));
        await Until(() => !page.ShowsDownload, "Download season went once every episode is on this PC");
        Assert.False(page.ShowsMoreMenu);
    }

    [Fact]
    public async Task ASeasonReadAgainAfterAPlayback_OffersTheEpisodesItNowLists()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1");
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: EpisodeId);
        var page = await FollowedAsync(SeasonId);
        page.Activate();
        Assert.False(page.ShowsDownload);
        // The sixth episode got its file meanwhile; a playback that stopped reads the season's episodes again.
        _detail.Get(ItemDetailFixture.SeasonItemEpisodesPath(SeasonId), ItemDetailFixture.Ok(TitlePayloads.SeasonEpisodes.Replace(
            "\"title\": \"Chapter Six\", \"runtime\": 50, \"files\": []",
            "\"title\": \"Chapter Six\", \"runtime\": 50, \"files\": [{ \"file_id\": \"4576\", \"hdr\": false, \"file_size\": 1073741824 }]",
            StringComparison.Ordinal)));
        _detail.Stops.Track(Task.CompletedTask);
        _detail.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await page.Refreshing;

        Assert.Equal((true, "Download season"), (page.ShowsDownload, page.DownloadLabel));
    }

    // A season's episode cards carry their Download as every film's and episode's card does (CardDownloadsTests): the page
    // adds no entries of its own, so the menu never has them twice.
    [Fact]
    public async Task ASeasonPage_AddsNoEntriesToItsEpisodeCards()
    {
        var page = await FollowedAsync(SeasonId);
        await Until(() => page.ShowsDownload, "Download season showed");

        Assert.IsNotAssignableFrom<ICardMenuSource>(page);
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

    // The app's downloads live as long as the app, and the title page listens to its view model, as x:Bind does: a view model
    // the downloads still held would keep the page in memory for good. ItemDetailPage stops following as it is left and as it
    // unloads; once it did, nothing of it is held.
    [Fact]
    public async Task ATitlePageThatWasLeft_IsNotKeptByTheDownloads()
    {
        var (page, loading) = ShowTitlePage();
        await loading;
        await Until(() => ShowsDownload(page), "Download showed");

        LeaveTitlePage(page);

        Assert.True(await Collected.IsGoneAsync(page));
        GC.KeepAlive(Manager);
    }

    // What the test above relies on: while the title page shows, the downloads do hold it.
    [Fact]
    public async Task ATitlePageThatShows_IsKeptByTheDownloads()
    {
        var (page, loading) = ShowTitlePage();
        await loading;
        await Until(() => ShowsDownload(page), "Download showed");

        Collect();

        Assert.True(page.TryGetTarget(out _));
        GC.KeepAlive(Manager);
    }

    // Made in a method of its own, so no local of the test keeps the page; it loads and follows as ItemDetailPage's
    // OnNavigatedTo does.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference<ShownTitlePage> Page, Task Loading) ShowTitlePage()
    {
        var model = Page();
        var page = new ShownTitlePage(model);
        var loading = model.LoadCommand.ExecuteAsync(MovieId);
        model.FollowDownloads();
        return (new WeakReference<ShownTitlePage>(page), loading);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ShowsDownload(WeakReference<ShownTitlePage> page) => page.TryGetTarget(out var shown) && shown.ViewModel.ShowsDownload;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeaveTitlePage(WeakReference<ShownTitlePage> page)
    {
        if (page.TryGetTarget(out var shown))
        {
            shown.ViewModel.StopFollowingDownloads();
        }
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Stands in for ItemDetailPage: it holds its view model and listens to it, as the page's bindings do.
    private sealed class ShownTitlePage
    {
        public ShownTitlePage(ItemDetailViewModel viewModel)
        {
            ViewModel = viewModel;
            viewModel.PropertyChanged += OnChanged;
        }

        public ItemDetailViewModel ViewModel { get; }

        public int Changes { get; private set; }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Changes++;
    }

    private static bool Progressed(IEnumerable<EventArgs> changes, string downloadId) =>
        changes.Any(e => e is DownloadProgressedEventArgs progressed && progressed.DownloadId == downloadId);

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
}
