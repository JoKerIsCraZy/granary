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
        new(_h.NewManager(queue), disk ?? _h.Disk, _h.Preferences, _navigation, _confirmation, dispatcher ?? new ImmediateDispatcher());

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
        Assert.Null(films.PosterPath);
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
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Queued);
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var page = await AttachedAsync(_h.StartQueue());
        var row = Row(page, "dl_1");
        await Until(() => row.StatusText == "Downloading · 0%", "the row said it is downloading");
        var sections = page.Sections;

        body.Release(1800);

        await Until(() => row.StatusText == "Downloading · 60%", "the row showed the progress");
        Assert.Same(sections, page.Sections);
        Assert.Same(row, Row(page, "dl_1"));
        Assert.True(row.ShowsProgress);
        Assert.Equal(0.6, row.Progress, 3);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        body.Release(600);

        await Until(() => row.StatusText == "Downloading · 80%", "the row showed the next progress");
        Assert.Same(sections, page.Sections);
        // 2400 bytes so far.
        Assert.StartsWith("2.3 KB used · ", page.SpaceText, StringComparison.Ordinal);
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

        Assert.Equal((true, false, false, true), Offers(Row(page, "dl_1")));
        Assert.Equal((false, true, false, false), Offers(Row(page, "dl_2")));
        Assert.Equal((false, false, true, false), Offers(Row(page, "dl_3")));
        Assert.Equal((false, false, false, false), Offers(Row(page, "dl_4")));
        Assert.Equal((false, false, false, false), Offers(Row(page, "dl_5")));
        Assert.Equal(["Downloading · 0%", "Paused", "Failed", "No longer available", "Downloaded"],
            new[] { "dl_1", "dl_2", "dl_3", "dl_4", "dl_5" }.Select(id => Row(page, id).StatusText));
        Assert.True(Row(page, "dl_1").PauseCommand.CanExecute(null));
        Assert.False(Row(page, "dl_5").PauseCommand.CanExecute(null));

        await Row(page, "dl_1").PauseCommand.ExecuteAsync(null);
        await Row(page, "dl_2").ResumeCommand.ExecuteAsync(null);
        await Row(page, "dl_3").RetryCommand.ExecuteAsync(null);

        Assert.True(_h.Find("dl_1")!.Record.IsPausedByViewer);
        Assert.False(_h.Find("dl_2")!.Record.IsPausedByViewer);
        Assert.Equal((DownloadState.Queued, DownloadFailure.None), (_h.Find("dl_3")!.Record.State, _h.Find("dl_3")!.Record.Failure));
        Assert.Equal(("Paused", true), (Row(page, "dl_1").StatusText, Row(page, "dl_1").CanResume));
        Assert.Equal(("Queued", true), (Row(page, "dl_2").StatusText, Row(page, "dl_2").CanPause));
        Assert.Equal(("Queued", false), (Row(page, "dl_3").StatusText, Row(page, "dl_3").CanRetry));

        static (bool Pause, bool Resume, bool Retry, bool Progress) Offers(DownloadRowViewModel row) =>
            (row.CanPause, row.CanResume, row.CanRetry, row.ShowsProgress);
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
    public async Task ARow_OpensItsTitlesPage()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        var page = await AttachedAsync();

        Row(page, "dl_15").OpenCommand.Execute(null);

        Assert.Equal([(AppRoute.Item, (object?)"ep_dl_15")], _navigation.Navigations);
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

    [Fact]
    public void TheSidebar_HasDownloadsUnderYourStuff_AfterHistory()
    {
        var entries = SidebarLayout.Entries;
        int history = entries.ToList().FindIndex(e => e.Tag == "List:history");

        Assert.Equal(new SidebarEntry("Downloads", "Downloads", SidebarSection.YourStuff, "Downloads"), entries[history + 1]);
        Assert.Equal(nameof(AppRoute.Downloads), entries[history + 1].Tag);
    }
}
