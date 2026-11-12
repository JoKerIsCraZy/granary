using System.ComponentModel;
using System.Runtime.CompilerServices;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The Downloads page: the active profile's films and series, a series by season, each row with its status and the apps'
/// Pause, Resume, Retry and Delete; the space used and free at the top, read off the UI thread; progress updates a row in place.
/// </summary>
public sealed class DownloadsViewModelTests : IAsyncDisposable
{
    private const long GB = 1L << 30;

    private readonly DownloadHarness _h = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeConfirmation _confirmation = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    // The page over the harness's downloads, on a UI thread that runs posted work at once unless the test gives its own.
    private DownloadsViewModel Page(DownloadQueue? queue = null, IUiDispatcher? dispatcher = null, IDiskSpace? disk = null) =>
        new(_h.NewManager(queue), disk ?? _h.Disk, _h.Preferences, _navigation, _confirmation, dispatcher ?? new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadsViewModel>());

    private async Task<DownloadsViewModel> AttachedAsync(DownloadQueue? queue = null)
    {
        var page = Page(queue);
        page.Attach();
        await page.SpaceRead;
        return page;
    }

    // A film kept here, its media item "mv_<id>".
    private Task<StoredDownload> AddFilmAsync(string id, string title, DownloadState state = DownloadState.Completed, long size = 3000,
        Func<DownloadRecord, DownloadRecord>? change = null) =>
        _h.AddAsync(id, state, size, mediaItemId: "mv_" + id, change: r => Then(change, r with { Title = title }));

    // An episode kept here, its media item "ep_<id>".
    private Task<StoredDownload> AddEpisodeAsync(string id, int? season, int? episode, string title, string seriesTitle = "Example Series",
        string seriesId = "sr_1", Func<DownloadRecord, DownloadRecord>? change = null) =>
        _h.AddAsync(id, DownloadState.Completed, mediaItemId: "ep_" + id, change: r => Then(change, r with
        {
            Title = title, SeriesId = seriesId, SeriesTitle = seriesTitle, SeasonNumber = season, EpisodeNumber = episode,
        }));

    private static DownloadRecord Then(Func<DownloadRecord, DownloadRecord>? change, DownloadRecord record) =>
        change is null ? record : change(record);

    private static IEnumerable<DownloadRowViewModel> Rows(DownloadsViewModel page) =>
        page.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Rows);

    private static DownloadRowViewModel Row(DownloadsViewModel page, string downloadId) =>
        Rows(page).Single(r => r.DownloadId == downloadId);

    [Fact]
    public async Task FilmsAndSeries_AreGrouped_SeriesBySeason()
    {
        await AddEpisodeAsync("dl_25", 2, 5, "Second Chapter Five");
        await AddFilmAsync("dl_m2", "Example Movie");
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", change: r => r with
        {
            ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg", ["series_poster"] = "series_poster.jpg" },
        });
        await AddFilmAsync("dl_m1", "Another Movie");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two");
        await AddEpisodeAsync("dl_a1", 1, 1, "Pilot", seriesTitle: "A Series", seriesId: "sr_2");

        var page = await AttachedAsync();

        Assert.Equal(["Films", "A Series", "Example Series"], page.Sections.Select(s => s.Title));
        var films = page.Sections[0];
        // Never null: an x:Bind function skips a null argument, and a recycled element would keep the last picture.
        Assert.Equal("", films.PosterPath);
        Assert.All(films.Groups.SelectMany(g => g.Rows), row => Assert.Equal("", row.PosterPath));
        var filmGroup = Assert.Single(films.Groups);
        Assert.Equal("", filmGroup.Title);
        Assert.Equal(["Another Movie", "Example Movie"], filmGroup.Rows.Select(r => r.Title));
        var series = page.Sections[2];
        Assert.Equal(Path.Combine(_h.Find("dl_15")!.Folder, "series_poster.jpg"), series.PosterPath);
        Assert.Equal(["Season 1", "Season 2"], series.Groups.Select(g => g.Title));
        Assert.Equal(["E2 · Chapter Two", "E5 · Chapter Five"], series.Groups[0].Rows.Select(r => r.Detail));
        var fifth = series.Groups[0].Rows[1];
        Assert.Equal(("Example Series", "ep_dl_15", Path.Combine(_h.Find("dl_15")!.Folder, "poster.jpg")), (fifth.Title, fifth.MediaItemId, fifth.PosterPath));
        Assert.Equal("", filmGroup.Rows[0].Detail);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task ASpecialsSeason_ComesLast_AsSpecials()
    {
        await AddEpisodeAsync("dl_s1", 0, 1, "Behind the Scenes");
        await AddEpisodeAsync("dl_21", 2, 1, "Return");
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot");

        var page = await AttachedAsync();

        Assert.Equal(["Season 1", "Season 2", "Specials"], Assert.Single(page.Sections).Groups.Select(g => g.Title));
    }

    [Fact]
    public async Task TheHeader_SaysTheSpaceUsedAndFree()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 12 * GB);
        _h.Disk.Set(200 * GB);

        var page = await AttachedAsync();

        Assert.Equal("12.0 GB used · 200.0 GB free", page.SpaceText);
        Assert.Equal([_h.Preferences.Folder], _h.Disk.Asked);
    }

    [Fact]
    public async Task ADriveThatIsNotThere_ShowsOnlyTheSpaceUsed()
    {
        _h.Disk.Set(null);

        var page = await AttachedAsync();

        Assert.Equal("0 B used", page.SpaceText);
    }

    [Fact]
    public async Task AFreeSpaceReadThatFails_ShowsOnlyTheSpaceUsed()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 12 * GB);
        var page = Page(disk: new FailingDiskSpace());

        page.Attach();
        await page.SpaceRead;

        Assert.Equal("12.0 GB used", page.SpaceText);
        Assert.Single(Rows(page));
        Assert.Contains(_h.Logs.Lines, line => line.Contains("free space where downloads go could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains(FailingDiskSpace.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreeSpaceReadThatHangs_IsUnknownAfterFiveSeconds()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 12 * GB);
        using var disk = new HeldDiskSpace(200 * GB);
        var page = Page(disk: disk);

        page.Attach();
        _h.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(page.SpaceRead.IsCompleted);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await page.SpaceRead;

        Assert.Equal("12.0 GB used", page.SpaceText);
        Assert.Contains(_h.Logs.Lines, line => line.Contains("free space where downloads go did not come within 5 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFreeSpace_IsReadOffTheUiThread_AndShownOnIt()
    {
        using var ui = new SerialDispatcher();
        var disk = new ThreadNotingDiskSpace(ui, 200 * GB);
        await AddFilmAsync("dl_1", "Example Movie", size: 12 * GB);
        var page = Page(dispatcher: ui, disk: disk);
        var shownOnUi = new List<bool>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadsViewModel.SpaceText))
            {
                shownOnUi.Add(ui.HasThreadAccess);
            }
        };

        await ui.RunAsync(() =>
        {
            page.Attach();
            return page.SpaceRead;
        });

        Assert.Equal([false], disk.ReadOnUi);
        Assert.Equal("12.0 GB used · 200.0 GB free", ui.Read(() => page.SpaceText));
        Assert.NotEmpty(shownOnUi);
        Assert.All(shownOnUi, Assert.True);
    }

    [Fact]
    public async Task Progress_UpdatesTheRowInPlace()
    {
        // A UI thread of its own, as the app's: the refreshes a running transfer posts run one after another, never side by side.
        using var ui = new SerialDispatcher();
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Queued);
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var page = Page(_h.StartQueue(), ui);
        await ui.RunAsync(() =>
        {
            page.Attach();
            return page.SpaceRead;
        });
        var row = ui.Read(() => Row(page, "dl_1"));
        await Until(() => ui.Read(() => row.StatusText) == "Downloading · 0%", "the row said it is downloading");
        var sections = ui.Read(() => page.Sections);

        body.Release(1800);

        await Until(() => ui.Read(() => row.StatusText) == "Downloading · 60%", "the row showed the progress");
        Assert.Same(sections, ui.Read(() => page.Sections));
        Assert.Same(row, ui.Read(() => Row(page, "dl_1")));
        Assert.True(ui.Read(() => row.ShowsProgress));
        Assert.Equal(0.6, ui.Read(() => row.Progress), 3);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        body.Release(600);

        await Until(() => ui.Read(() => row.StatusText) == "Downloading · 80%", "the row showed the next progress");
        Assert.Same(sections, ui.Read(() => page.Sections));
        // 2400 bytes so far.
        Assert.StartsWith("2.3 KB used · ", ui.Read(() => page.SpaceText), StringComparison.Ordinal);
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public async Task ARunningRow_OffersPause_APausedOneResume_AFailedOneRetry()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Downloading);
        await AddFilmAsync("dl_2", "Example Movie Two", DownloadState.Queued, change: r => r with { IsPausedByViewer = true });
        await AddFilmAsync("dl_3", "Example Movie Three", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.Server });
        await AddFilmAsync("dl_4", "Example Movie Four", DownloadState.Failed, change: r => r with { IsNoLongerAvailable = true });
        await AddFilmAsync("dl_5", "Example Movie Five");
        var page = await AttachedAsync();

        Assert.Equal((DownloadRowAction.Pause, "Pause", true, true), Offers(Row(page, "dl_1")));
        Assert.Equal((DownloadRowAction.Resume, "Resume", true, false), Offers(Row(page, "dl_2")));
        Assert.Equal((DownloadRowAction.Retry, "Retry", true, false), Offers(Row(page, "dl_3")));
        Assert.Equal((DownloadRowAction.None, "", false, false), Offers(Row(page, "dl_4")));
        Assert.Equal((DownloadRowAction.None, "", false, false), Offers(Row(page, "dl_5")));
        Assert.Equal(["Downloading · 0%", "Paused", "Failed", "No longer available", "Downloaded"],
            new[] { "dl_1", "dl_2", "dl_3", "dl_4", "dl_5" }.Select(id => Row(page, id).StatusText));
        Assert.True(Row(page, "dl_1").PrimaryActionCommand.CanExecute(null));
        Assert.False(Row(page, "dl_5").PrimaryActionCommand.CanExecute(null));

        await Row(page, "dl_1").PrimaryActionCommand.ExecuteAsync(null);
        await Row(page, "dl_2").PrimaryActionCommand.ExecuteAsync(null);
        await Row(page, "dl_3").PrimaryActionCommand.ExecuteAsync(null);

        Assert.True(_h.Find("dl_1")!.Record.IsPausedByViewer);
        Assert.False(_h.Find("dl_2")!.Record.IsPausedByViewer);
        Assert.Equal((DownloadState.Queued, DownloadFailure.None), (_h.Find("dl_3")!.Record.State, _h.Find("dl_3")!.Record.Failure));
        Assert.Equal(("Paused", DownloadRowAction.Resume), (Row(page, "dl_1").StatusText, Row(page, "dl_1").PrimaryAction));
        Assert.Equal(("Queued", DownloadRowAction.Pause), (Row(page, "dl_2").StatusText, Row(page, "dl_2").PrimaryAction));
        Assert.Equal(("Queued", DownloadRowAction.Pause), (Row(page, "dl_3").StatusText, Row(page, "dl_3").PrimaryAction));

        static (DownloadRowAction Action, string Label, bool HasAction, bool Progress) Offers(DownloadRowViewModel row) =>
            (row.PrimaryAction, row.PrimaryActionLabel, row.HasPrimaryAction, row.ShowsProgress);
    }

    [Fact]
    public async Task AStateChange_KeepsTheSectionsTheRowAndItsButton()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Downloading);
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        var page = await AttachedAsync();
        var sections = page.Sections;
        var row = Row(page, "dl_1");
        var button = row.PrimaryActionCommand;

        await button.ExecuteAsync(null);
        Assert.Equal(("Paused", DownloadRowAction.Resume), (row.StatusText, row.PrimaryAction));
        await button.ExecuteAsync(null);
        Assert.Equal(("Downloading · 0%", DownloadRowAction.Pause), (row.StatusText, row.PrimaryAction));
        Assert.Single(_h.Disk.Asked);
        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { State = DownloadState.Completed }, Token);
        await page.SpaceRead;

        Assert.Equal(("Downloaded", DownloadRowAction.None), (row.StatusText, row.PrimaryAction));
        Assert.Same(sections, page.Sections);
        Assert.Same(row, Row(page, "dl_1"));
        Assert.Same(button, row.PrimaryActionCommand);
        // The finished file counts as used, and the drive's free space is read again for it.
        Assert.StartsWith("5.9 KB used · ", page.SpaceText, StringComparison.Ordinal);
        Assert.Equal(2, _h.Disk.Asked.Count);
    }

    [Fact]
    public async Task TheRowsButton_StaysPressableWhileItsPressRuns()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Downloading);
        var page = await AttachedAsync();
        var row = Row(page, "dl_1");
        var storeBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _h.Store.FolderMade = _ =>
        {
            storeBusy.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        // Another download being added holds the store's writes, so the pause below waits to be written.
        var adding = AddFilmAsync("dl_2", "Example Movie Two");
        await storeBusy.Task;

        var pausing = row.PrimaryActionCommand.ExecuteAsync(null);

        Assert.False(pausing.IsCompleted);
        Assert.Equal(DownloadRowAction.Resume, row.PrimaryAction);
        Assert.True(row.PrimaryActionCommand.CanExecute(null));
        release.Set();
        await pausing;
        await adding;
    }

    [Fact]
    public async Task Delete_AsksFirst_AndKeepsTheRowWhenDeclined()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();
        _confirmation.Answer = false;

        await Row(page, "dl_1").DeleteCommand.ExecuteAsync(null);

        Assert.Equal(("Delete download?", "This removes the downloaded copy from this PC. Nothing is deleted from your Silo server.", "Delete"),
            Assert.Single(_confirmation.Asked));
        Assert.NotNull(_h.Find("dl_1"));
        Assert.Single(Rows(page));

        _confirmation.Answer = true;
        await Row(page, "dl_1").DeleteCommand.ExecuteAsync(null);

        Assert.Null(_h.Find("dl_1"));
        Assert.Empty(page.Sections);
        Assert.True(page.IsEmpty);
    }

    [Fact]
    public async Task WatchedDownloads_AreSuggestedForDeletion()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", change: r => r with { IsWatched = true, ExpectedBytes = GB });
        await AddFilmAsync("dl_2", "Unwatched Movie", size: 5 * GB);
        // Not whole yet, or no longer on the server (it could not be downloaded again): neither counts.
        await AddFilmAsync("dl_3", "Arriving Movie", DownloadState.Downloading, 7 * GB, r => r with { IsWatched = true });
        await AddFilmAsync("dl_4", "Revoked Movie", size: 7 * GB, change: r => r with { IsWatched = true, IsNoLongerAvailable = true });

        var page = await AttachedAsync();
        _confirmation.Answer = false;

        Assert.True(page.ShowsReclaim);
        await page.ReclaimCommand.ExecuteAsync(null);
        // Two of them, and the space their files take: not the one that still arrives, nor the one the server no longer has.
        Assert.Equal("Delete 2 watched downloads and free 3.0 GB?", Assert.Single(_confirmation.Asked).Message);
    }

    [Fact]
    public async Task WithoutWatchedDownloads_NothingIsSuggested()
    {
        await AddFilmAsync("dl_2", "Unwatched Movie", size: 5 * GB);

        var page = await AttachedAsync();

        Assert.False(page.ShowsReclaim);
        Assert.False(page.ReclaimCommand.CanExecute(null));
    }

    [Fact]
    public async Task KeepWatched_HidesTheSuggestion_AtOnce()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        Assert.True(page.ShowsReclaim);

        await _h.Preferences.SetKeepWatchedAsync(true);
        Assert.False(page.ShowsReclaim);

        await _h.Preferences.SetKeepWatchedAsync(false);
        Assert.True(page.ShowsReclaim);
    }

    [Fact]
    public async Task ADownloadThatBecomesWatched_IsSuggested_WithoutThePageBeingMadeAgain()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB);
        var page = await AttachedAsync();
        var sections = page.Sections;

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsWatched = true }, Token);

        Assert.True(page.ShowsReclaim);
        Assert.Same(sections, page.Sections);
    }

    [Fact]
    public async Task TheSuggestion_DeletesOnlyAfterTheQuestion()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", change: r => r with { IsWatched = true, ExpectedBytes = GB });
        await AddFilmAsync("dl_2", "Unwatched Movie", size: 5 * GB);
        var page = await AttachedAsync(_h.StartQueue());
        _confirmation.Answer = false;

        await page.ReclaimCommand.ExecuteAsync(null);

        // The Android app's words, whole: its dialog's title, its question and its yes.
        Assert.Equal(("Reclaim Watched", "Delete 2 watched downloads and free 3.0 GB?", "Reclaim"), Assert.Single(_confirmation.Asked));
        Assert.Equal(3, Rows(page).Count());

        _confirmation.Answer = true;
        await page.ReclaimCommand.ExecuteAsync(null);

        Assert.Equal(["dl_2"], Rows(page).Select(r => r.DownloadId));
        Assert.Null(_h.Find("dl_1"));
        Assert.Null(_h.Find("dl_15"));
        Assert.False(page.ShowsReclaim);
        // The server hears of each, once its files are gone.
        await Until(() => _h.Silo.Sent.Count(r => r.Method == HttpMethod.Delete) == 2, "both deletes reached the server");
    }

    [Fact]
    public async Task OneWatchedDownload_IsAskedAboutInTheSingular()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        _confirmation.Answer = false;

        await page.ReclaimCommand.ExecuteAsync(null);

        Assert.Equal("Delete 1 watched download and free 2.0 GB?", _confirmation.Asked[0].Message);
    }

    [Fact]
    public async Task ADownloadThatStoppedBeingWatchedWhileTheQuestionWasOpen_Stays()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_2", "Another Movie", size: GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_3", "Later Movie", size: GB);
        var page = await AttachedAsync();
        // While the question is open the server's word arrives: one is not watched after all, and another became watched.
        _confirmation.WhileAsking = async () =>
        {
            await _h.Store.UpdateAsync(Scope7, "dl_2", r => r with { IsWatched = false }, Token);
            await _h.Store.UpdateAsync(Scope7, "dl_3", r => r with { IsWatched = true }, Token);
        };

        await page.ReclaimCommand.ExecuteAsync(null);

        // Only what the question named and is still watched goes.
        Assert.Null(_h.Find("dl_1"));
        Assert.NotNull(_h.Find("dl_2"));
        Assert.NotNull(_h.Find("dl_3"));
    }

    [Fact]
    public async Task ARow_OpensItsTitlesPage()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        var page = await AttachedAsync();

        Row(page, "dl_15").OpenCommand.Execute(null);

        Assert.Equal([(AppRoute.Item, (object?)"ep_dl_15")], _navigation.Navigations);
    }

    [Fact]
    public async Task ADownloadedRow_OffersPlay_WhichStartsThePlayerWhereTheProfileLeftIt()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        await AddFilmAsync("dl_m1", "Example Movie", DownloadState.Downloading);
        var page = await AttachedAsync();
        var row = Row(page, "dl_15");

        Assert.True(row.CanPlay);
        Assert.False(Row(page, "dl_m1").CanPlay);
        Assert.False(Row(page, "dl_m1").PlayCommand.CanExecute(null));
        row.PlayCommand.Execute(null);

        var (route, parameter) = Assert.Single(_navigation.Navigations);
        Assert.Equal(AppRoute.Player, route);
        Assert.Equal(Granary.Core.Playback.PlayRequest.Resume("ep_dl_15"), parameter);
    }

    [Fact]
    public async Task NoDownloads_SaysHowToGetSome()
    {
        var page = await AttachedAsync();

        Assert.True(page.IsEmpty);
        Assert.Empty(page.Sections);
        Assert.Equal("No downloads yet", DownloadsViewModel.EmptyTitle);
        Assert.Equal("Choose Download in a film's or episode's ⋯ menu to keep it on this PC and watch it without your Silo server.",
            DownloadsViewModel.EmptyText);
    }

    [Fact]
    public async Task ADownloadAdded_ShowsInItsSection()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();

        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");

        Assert.Equal(["Films", "Example Series"], page.Sections.Select(s => s.Title));
    }

    [Fact]
    public async Task Detach_StopsFollowingTheDownloads()
    {
        var page = await AttachedAsync();

        page.Detach();
        await AddFilmAsync("dl_1", "Example Movie");

        Assert.Empty(page.Sections);
        Assert.True(page.IsEmpty);
        page.Attach();
        Assert.Single(Rows(page));
    }

    // The app's downloads live as long as the app. The page listens to its view model and the rows, as x:Bind does, so a view
    // model they still held would keep the page in memory for good, cached or not: once it left, nothing of it is held.
    [Fact]
    public async Task APageThatWasLeft_IsNotKeptByTheDownloads()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var downloads = _h.NewManager();
        var (page, spaceRead) = ShowPage(downloads);
        await spaceRead;

        Leave(page);

        Assert.True(await Collected.IsGoneAsync(page));
        GC.KeepAlive(downloads);
    }

    // What the test above relies on: while the page shows, the downloads do hold it.
    [Fact]
    public async Task APageThatShows_IsKeptByTheDownloads()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var downloads = _h.NewManager();
        var (page, spaceRead) = ShowPage(downloads);
        await spaceRead;

        Collect();

        Assert.True(page.TryGetTarget(out _));
        GC.KeepAlive(downloads);
    }

    // Made in a method of its own, so no local of the test keeps the page.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference<ShownPage> Page, Task SpaceRead) ShowPage(DownloadManager downloads)
    {
        var model = new DownloadsViewModel(downloads, _h.Disk, _h.Preferences, _navigation, _confirmation, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadsViewModel>());
        var page = new ShownPage(model);
        model.Attach();
        page.FollowRows();
        Assert.NotEmpty(Rows(model));
        return (new WeakReference<ShownPage>(page), model.SpaceRead);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Leave(WeakReference<ShownPage> page)
    {
        if (page.TryGetTarget(out var shown))
        {
            shown.ViewModel.Detach();
        }
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Stands in for DownloadsPage: it holds its view model and listens to it and to its rows, as the page's bindings do.
    private sealed class ShownPage
    {
        public ShownPage(DownloadsViewModel viewModel)
        {
            ViewModel = viewModel;
            viewModel.PropertyChanged += OnChanged;
        }

        public DownloadsViewModel ViewModel { get; }

        public int Changes { get; private set; }

        public void FollowRows()
        {
            foreach (var row in Rows(ViewModel))
            {
                row.PropertyChanged += OnChanged;
            }
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Changes++;
    }

    [Fact]
    public void TheSidebar_HasDownloadsUnderYourStuff_AfterHistory()
    {
        var entries = SidebarLayout.Entries;
        int history = entries.ToList().FindIndex(e => e.Tag == "List:history");

        Assert.Equal(new SidebarEntry("Downloads", "Downloads", SidebarSection.YourStuff, "Downloads"), entries[history + 1]);
        Assert.Equal(nameof(AppRoute.Downloads), entries[history + 1].Tag);
    }

    [Fact]
    public async Task ADownloadThatPlays_IsNotSuggestedForDeletion_UntilItsPlayEnded()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_2", "Another Movie", size: GB, change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        Assert.True(page.ShowsReclaim);

        // Past its credits' start under the minimized player: watched, and still open.
        using (_h.Playing.Enter(Scope7, "dl_1"))
        {
            await page.ReclaimCommand.ExecuteAsync(null);

            Assert.Equal("Delete 1 watched download and free 1.0 GB?", Assert.Single(_confirmation.Asked).Message);
            Assert.NotNull(_h.Find("dl_1"));
            Assert.Null(_h.Find("dl_2"));
            Assert.False(page.ShowsReclaim);
        }

        // Its play ended: it is offered now.
        Assert.True(page.ShowsReclaim);
    }

    [Fact]
    public async Task ADownloadThatStartedPlayingWhileTheQuestionWasOpen_Stays()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        IDisposable? play = null;
        _confirmation.WhileAsking = () =>
        {
            play = _h.Playing.Enter(Scope7, "dl_1");
            return Task.CompletedTask;
        };

        await page.ReclaimCommand.ExecuteAsync(null);

        // Its file is not taken from under the player.
        Assert.NotNull(_h.Find("dl_1"));
        play!.Dispose();
    }

    [Fact]
    public async Task ATitleTheServerHasNotConfirmedAsWatched_IsNotSuggestedForDeletion()
    {
        // Left past nine tenths without the server: it starts over next time, but nothing says the server counts it as watched.
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { ResumeMs = 0, ResumeAt = ManualTimeProvider.DefaultStart });
        var page = await AttachedAsync();

        Assert.False(page.ShowsReclaim);

        // The server's progress was read: it is watched.
        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsWatched = true }, Token);

        Assert.True(page.ShowsReclaim);
    }
}
