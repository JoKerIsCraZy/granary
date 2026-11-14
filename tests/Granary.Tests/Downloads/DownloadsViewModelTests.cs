using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The Downloads page: the active profile's films and series as cards, each with its size and status, a series as one card
/// that opens its episodes; the apps' Play, Pause, Resume, Retry and Delete in the cards' menus; the space used and free at the
/// top, read off the UI thread; progress and state change a card in place.
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
        string seriesId = "sr_1", DownloadState state = DownloadState.Completed, Func<DownloadRecord, DownloadRecord>? change = null) =>
        _h.AddAsync(id, state, mediaItemId: "ep_" + id, change: r => Then(change, r with
        {
            Title = title, SeriesId = seriesId, SeriesTitle = seriesTitle, SeasonNumber = season, EpisodeNumber = episode,
        }));

    private static DownloadRecord Then(Func<DownloadRecord, DownloadRecord>? change, DownloadRecord record) =>
        change is null ? record : change(record);

    // Every card on the page: the films', then the series'.
    private static IEnumerable<LiveCard> Cards(DownloadsViewModel page) => page.Films.Concat(page.Series);

    private static LiveCard Live(DownloadsViewModel page, string id) => Cards(page).Single(c => c.Card.Id == id);

    private static MediaCard Card(DownloadsViewModel page, string id) => Live(page, id).Card;

    private static List<string> Menu(DownloadsViewModel page, string id) => [.. page.Actions.EntriesFor(Card(page, id)).Select(e => e.Label)];

    // Picks an entry of a card's ⋮ menu, as the menu runs it: with the card.
    private static async Task PickAsync(DownloadsViewModel page, string id, string label)
    {
        var card = Card(page, id);
        var entry = page.Actions.EntriesFor(card).Single(e => e.Label == label);
        if (entry.Command is IAsyncRelayCommand command)
        {
            await command.ExecuteAsync(card);
        }
        else
        {
            entry.Command.Execute(card);
        }
    }

    [Fact]
    public async Task FilmsAndSeries_AreCards_TheFilmsByTitle_ThenEachSeriesByTitle()
    {
        await AddEpisodeAsync("dl_25", 2, 5, "Second Chapter Five");
        await AddFilmAsync("dl_m2", "Example Movie", change: r => r with
        {
            ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg" },
        });
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", change: r => r with
        {
            ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg", ["series_poster"] = "series_poster.jpg" },
        });
        await AddFilmAsync("dl_m1", "Another Movie");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two");
        await AddEpisodeAsync("dl_a1", 1, 1, "Pilot", seriesTitle: "A Series", seriesId: "sr_2");

        var page = await AttachedAsync();

        Assert.Equal(["Another Movie", "Example Movie"], page.Films.Select(c => c.Card.Title));
        Assert.Equal(["A Series", "Example Series"], page.Series.Select(c => c.Card.Title));
        Assert.True(page.HasFilms);
        Assert.True(page.HasSeries);
        Assert.False(page.IsEmpty);
        // Posters, from the files on this PC: the film's own, the series' from its episodes.
        var film = Card(page, "dl_m2");
        Assert.Equal((MediaCardShape.Poster, DownloadCards.DownloadType, Path.Combine(_h.Find("dl_m2")!.Folder, "poster.jpg"), (Uri?)null),
            (film.Shape, film.Type, film.LocalImagePath, film.ImageUrl));
        var series = Card(page, "sr_1");
        Assert.Equal((MediaCardShape.Poster, DownloadCards.SeriesType, Path.Combine(_h.Find("dl_15")!.Folder, "series_poster.jpg")),
            (series.Shape, series.Type, series.LocalImagePath));
        Assert.Null(Card(page, "dl_m1").LocalImagePath);
    }

    [Fact]
    public async Task AFilmsCard_SaysItsSize_AndItsStatus_AndPlaysOnceItIsWhole()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 8 * GB);
        await AddFilmAsync("dl_2", "Arriving Movie", DownloadState.Queued, size: 2 * GB);

        var page = await AttachedAsync();

        var whole = Card(page, "dl_1");
        Assert.Equal(("Example Movie", "8.0 GB", "Downloaded", "mv_dl_1"), (whole.Title, whole.Subtitle, whole.Caption, whole.PlayContentId));
        // Screen readers hear the status, which is in no line they read otherwise.
        Assert.Equal("Example Movie, 8.0 GB, Downloaded", whole.AccessibleName);
        var arriving = Card(page, "dl_2");
        Assert.Equal(("2.0 GB", "Queued", (string?)null), (arriving.Subtitle, arriving.Caption, arriving.PlayContentId));
    }

    [Fact]
    public async Task ACardWhoseSizeIsNotKnownYet_KeepsItsLine()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Preparing, size: 0);

        var page = await AttachedAsync();

        // A grid sizes its cards by its first: every card keeps its three lines.
        Assert.Equal("\u00A0", Card(page, "dl_1").Subtitle);
        Assert.Equal("Example Movie, Preparing…", Card(page, "dl_1").AccessibleName);
    }

    [Fact]
    public async Task EachState_IsWordedAsThePageWordsIt_AndTroubleIsMarked()
    {
        await AddFilmAsync("dl_1", "Movie One", DownloadState.Downloading);
        await AddFilmAsync("dl_2", "Movie Two", DownloadState.Queued, change: r => r with { IsPausedByViewer = true });
        await AddFilmAsync("dl_3", "Movie Three", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.Server });
        await AddFilmAsync("dl_4", "Movie Four", DownloadState.Failed, change: r => r with { IsNoLongerAvailable = true });
        await AddFilmAsync("dl_5", "Movie Five");
        await AddFilmAsync("dl_6", "Movie Six", DownloadState.Queued);
        await AddFilmAsync("dl_7", "Movie Seven", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.NotEnoughSpace });

        var page = await AttachedAsync();

        string[] ids = ["dl_1", "dl_2", "dl_3", "dl_4", "dl_5", "dl_6", "dl_7"];
        // The whole status line: a card's tip until it is whole, its caption once it is.
        Assert.Equal(["Downloading · 0%", "Paused", "Failed", "No longer available", "Downloaded", "Queued", "Failed · not enough space"],
            ids.Select(id => Card(page, id).ToolTip ?? Card(page, id).Caption));
        Assert.Equal([false, false, true, true, false, false, true], ids.Select(id => Card(page, id).IsCaptionAttention));
    }

    [Fact]
    public async Task ACardsMenu_HoldsWhatTheListsButtonsDid()
    {
        await AddFilmAsync("dl_1", "Movie One", DownloadState.Downloading);
        await AddFilmAsync("dl_2", "Movie Two", DownloadState.Queued, change: r => r with { IsPausedByViewer = true });
        await AddFilmAsync("dl_3", "Movie Three", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.Server });
        await AddFilmAsync("dl_4", "Movie Four", DownloadState.Failed, change: r => r with { IsNoLongerAvailable = true });
        await AddFilmAsync("dl_5", "Movie Five");

        var page = await AttachedAsync();

        Assert.Equal(["Pause", "More Info", "Delete download"], Menu(page, "dl_1"));
        Assert.Equal(["Resume", "More Info", "Delete download"], Menu(page, "dl_2"));
        Assert.Equal(["Retry", "More Info", "Delete download"], Menu(page, "dl_3"));
        Assert.Equal(["More Info", "Delete download"], Menu(page, "dl_4"));
        Assert.Equal(["Play", "More Info", "Delete download"], Menu(page, "dl_5"));
    }

    [Fact]
    public async Task PauseResumeAndRetry_ChangeTheCardInPlace()
    {
        await AddFilmAsync("dl_1", "Movie One", DownloadState.Downloading);
        await AddFilmAsync("dl_2", "Movie Two", DownloadState.Queued, change: r => r with { IsPausedByViewer = true });
        await AddFilmAsync("dl_3", "Movie Three", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.Server });
        var page = await AttachedAsync();
        var films = page.Films;
        var cards = films.ToList();

        await PickAsync(page, "dl_1", "Pause");
        await PickAsync(page, "dl_2", "Resume");
        await PickAsync(page, "dl_3", "Retry");

        Assert.True(_h.Find("dl_1")!.Record.IsPausedByViewer);
        Assert.False(_h.Find("dl_2")!.Record.IsPausedByViewer);
        Assert.Equal((DownloadState.Queued, DownloadFailure.None), (_h.Find("dl_3")!.Record.State, _h.Find("dl_3")!.Record.Failure));
        Assert.Equal(["Paused", "Queued", "Queued"], new[] { "dl_1", "dl_2", "dl_3" }.Select(id => Card(page, id).Caption));
        Assert.Equal(["Resume", "More Info", "Delete download"], Menu(page, "dl_1"));
        Assert.Equal(["Pause", "More Info", "Delete download"], Menu(page, "dl_3"));
        // The same cards, each in its place: the one the menu was opened on keeps the focus.
        Assert.Same(films, page.Films);
        Assert.Equal(cards, page.Films);
    }

    [Fact]
    public async Task ADownloadThatFinishes_KeepsItsCard_AndTheFreeSpaceIsReadAgain()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Downloading);
        var page = await AttachedAsync();
        var card = Live(page, "dl_1");
        Assert.Single(_h.Disk.Asked);

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { State = DownloadState.Completed }, Token);
        await page.SpaceRead;

        Assert.Same(card, Live(page, "dl_1"));
        Assert.Equal(("Downloaded", "mv_dl_1"), (card.Card.Caption, card.Card.PlayContentId));
        // The finished file counts as used, and the drive's free space is read again for it.
        Assert.StartsWith("2.9 KB used · ", page.SpaceText, StringComparison.Ordinal);
        Assert.Equal(2, _h.Disk.Asked.Count);
    }

    [Fact]
    public async Task Progress_UpdatesTheCardInPlace()
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
        var card = ui.Read(() => Live(page, "dl_1"));
        await Until(() => ui.Read(() => card.Card.Subtitle) == "Downloading · 0%", "the card said it is downloading");

        body.Release(1800);

        await Until(() => ui.Read(() => card.Card.Subtitle) == "Downloading · 60%", "the card showed the progress");
        Assert.Same(card, ui.Read(() => Live(page, "dl_1")));
        Assert.Equal(0.6, ui.Read(() => card.Card.Progress) ?? 0, 3);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        body.Release(600);

        await Until(() => ui.Read(() => card.Card.Subtitle) == "Downloading · 80%", "the card showed the next progress");
        Assert.Same(card, ui.Read(() => Live(page, "dl_1")));
        // 2400 bytes so far.
        Assert.StartsWith("2.3 KB used · ", ui.Read(() => page.SpaceText), StringComparison.Ordinal);
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public async Task ADownloadingCard_SaysItsSpeedAndTimeLeft_OnceThereIsEnoughToMeasure_AndNotOnceItStops()
    {
        using var ui = new SerialDispatcher();
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Queued);
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var queue = _h.StartQueue();
        var page = Page(queue, ui);
        await ui.RunAsync(() =>
        {
            page.Attach();
            return page.SpaceRead;
        });
        var card = ui.Read(() => Live(page, "dl_1"));
        await Until(() => ui.Read(() => card.Card.Subtitle) == "Downloading · 0%", "the card said it is downloading");
        long sent = 0;
        async Task SendAsync(long count)
        {
            sent += count;
            body.Release(count);
            await Until(() => queue.BytesDone(Scope7, "dl_1") == sent, sent + " bytes arrived");
        }

        // 300 bytes a second, each second's bytes as the clock moves on; the page reads the speed once a second.
        await SendAsync(300);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(300);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(300);
        await Until(() => ui.Read(() => card.Card.Subtitle) == "Downloading · 30%", "the card showed the progress");
        Assert.Equal("2.9 KB", ui.Read(() => card.Card.Caption));

        _h.Time.Advance(TimeSpan.FromSeconds(1));

        // 2100 bytes to go at 300 a second: how far and how long, then how fast; the whole line in the card's tip.
        await Until(() => ui.Read(() => card.Card.Caption) == "300 B/s", "the card said its speed");
        Assert.Equal(("30% · under 1 min left", "Downloading · 30% · 300 B/s · under 1 min left"),
            ui.Read(() => (card.Card.Subtitle, card.Card.ToolTip)));
        Assert.Same(card, ui.Read(() => Live(page, "dl_1")));
        // Screen readers hear neither the speed nor the percentage again at every step.
        Assert.Equal("Example Movie, 2.9 KB, Downloading", ui.Read(() => card.Card.AccessibleName));

        // Nothing comes for more than three seconds: the speed goes, as no progress would tell.
        _h.Time.Advance(TimeSpan.FromSeconds(4));

        await Until(() => ui.Read(() => (card.Card.Subtitle, card.Card.Caption)) == ("Downloading · 30%", "2.9 KB"), "the card dropped the speed");
        body.ReleaseAll();
        await Until(() => ui.Read(() => card.Card.Caption) == "Downloaded", "the download finished");
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public async Task Delete_AsksFirst_AndKeepsTheCardWhenDeclined()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();
        _confirmation.Answer = false;

        await PickAsync(page, "dl_1", "Delete download");

        Assert.Equal(("Delete download?", "This removes the downloaded copy from this PC. Nothing is deleted from your Silo server.", "Delete"),
            Assert.Single(_confirmation.Asked));
        Assert.NotNull(_h.Find("dl_1"));
        Assert.Single(Cards(page));

        _confirmation.Answer = true;
        await PickAsync(page, "dl_1", "Delete download");

        Assert.Null(_h.Find("dl_1"));
        Assert.Empty(Cards(page));
        Assert.False(page.HasFilms);
        Assert.True(page.IsEmpty);
    }

    [Fact]
    public async Task AWholeDownloadsCard_PlaysWhereTheProfileLeftIt_AndAnyOtherOpensItsPage()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        await AddFilmAsync("dl_2", "Arriving Movie", DownloadState.Downloading);
        var page = await AttachedAsync();

        page.OpenCardCommand.Execute(Card(page, "dl_1"));
        page.OpenCardCommand.Execute(Card(page, "dl_2"));

        Assert.Equal([(AppRoute.Player, (object?)PlayRequest.Resume("mv_dl_1")), (AppRoute.Item, "mv_dl_2")], _navigation.Navigations);
    }

    [Fact]
    public async Task PlayAndMoreInfo_InTheMenu_PlayItAndOpenItsPage()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();

        await PickAsync(page, "dl_1", "Play");
        await PickAsync(page, "dl_1", "More Info");

        Assert.Equal([(AppRoute.Player, (object?)PlayRequest.Resume("mv_dl_1")), (AppRoute.Item, "mv_dl_1")], _navigation.Navigations);
    }

    [Fact]
    public async Task AWholeDownloadsCard_ShowsHowMuchWasWatched_AsContinueWatchingDoes()
    {
        // Two hours long: half an hour in, watched, never started, and one the server no longer has, a quarter in.
        await AddFilmAsync("dl_1", "Started Movie", change: r => r with { ResumeMs = 1_800_000, DurationMs = 7_200_000 });
        await AddFilmAsync("dl_2", "Watched Movie", change: r => r with { IsWatched = true, ResumeMs = 0, DurationMs = 7_200_000 });
        await AddFilmAsync("dl_3", "New Movie", change: r => r with { DurationMs = 7_200_000 });
        await AddFilmAsync("dl_4", "Revoked Movie", change: r => r with { IsNoLongerAvailable = true, ResumeMs = 1_800_000, DurationMs = 7_200_000 });

        var page = await AttachedAsync();

        var started = Card(page, "dl_1");
        Assert.Equal(("90 min left", 0.25, false, false), (started.Caption, started.Progress, started.IsWatched, started.ShowsWatchedMark));
        Assert.Equal("Started Movie, 2.9 KB, 90 min left", started.AccessibleName);
        var watched = Card(page, "dl_2");
        Assert.Equal(("Watched", (double?)null, true, true), (watched.Caption, watched.Progress, watched.IsWatched, watched.ShowsWatchedMark));
        var unstarted = Card(page, "dl_3");
        Assert.Equal(("Downloaded", (double?)null, false), (unstarted.Caption, unstarted.Progress, unstarted.IsWatched));
        // Its status comes first; the bar still says how far it was watched.
        var revoked = Card(page, "dl_4");
        Assert.Equal(("No longer available", 0.25), (revoked.Caption, revoked.Progress));
    }

    [Fact]
    public async Task APositionKept_MovesTheCardsWatchProgress_InPlace_AlsoWithoutTheServer()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();
        var card = Live(page, "dl_1");
        Assert.Equal("Downloaded", card.Card.Caption);

        // A play from this PC keeps where the viewer is in the download's record, as it goes.
        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with
        {
            ResumeMs = 3_600_000, ResumeAt = ManualTimeProvider.DefaultStart, DurationMs = 7_200_000,
        }, Token);

        Assert.Same(card, Live(page, "dl_1"));
        Assert.Equal(("60 min left", 0.5), (card.Card.Caption, card.Card.Progress));

        // The server's progress, read back once it was reached: watched.
        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { ResumeMs = 0, IsWatched = true }, Token);

        Assert.Equal(("Watched", (double?)null, true), (card.Card.Caption, card.Card.Progress, card.Card.ShowsWatchedMark));
    }

    [Fact]
    public async Task AStartedOrWatchedDownload_OffersPlayFromBeginning()
    {
        await AddFilmAsync("dl_1", "Started Movie", change: r => r with { ResumeMs = 1_800_000, DurationMs = 7_200_000 });
        await AddFilmAsync("dl_2", "Watched Movie", change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_3", "New Movie");
        var page = await AttachedAsync();

        Assert.Equal(["Play", "Play from Beginning", "More Info", "Delete download"], Menu(page, "dl_1"));
        Assert.Equal(["Play", "Play from Beginning", "More Info", "Delete download"], Menu(page, "dl_2"));
        Assert.Equal(["Play", "More Info", "Delete download"], Menu(page, "dl_3"));

        await PickAsync(page, "dl_1", "Play from Beginning");

        Assert.Equal([(AppRoute.Player, (object?)PlayRequest.FromStart("mv_dl_1"))], _navigation.Navigations);
    }

    [Fact]
    public async Task ASeriesWhoseEpisodesAreAllWatched_IsMarkedWatched()
    {
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot", change: r => r with { IsWatched = true });
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two", change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        Assert.Equal((true, true), (Card(page, "sr_1").IsWatched, Card(page, "sr_1").ShowsWatchedMark));

        await _h.Store.UpdateAsync(Scope7, "dl_12", r => r with { IsWatched = false, ResumeMs = 60_000 }, Token);

        Assert.False(Card(page, "sr_1").IsWatched);
    }

    [Fact]
    public async Task ASeriesCard_CountsItsEpisodes_AndOpensThem()
    {
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two");
        var page = await AttachedAsync();

        var card = Card(page, "sr_1");
        Assert.Equal(("Example Series", "5.9 KB", "2 episodes", (double?)null, (string?)null),
            (card.Title, card.Subtitle, card.Caption, card.Progress, card.PlayContentId));
        page.OpenCardCommand.Execute(card);

        Assert.Equal([(AppRoute.DownloadedSeries, (object?)"sr_1")], _navigation.Navigations);
        Assert.Equal(["More Info", "Delete All Episodes"], Menu(page, "sr_1"));
    }

    [Fact]
    public async Task ASeriesCard_SaysHowManyAreWhole_AndHowMuchOfThemIsHere_WhileTheyCome()
    {
        using var ui = new SerialDispatcher();
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two", state: DownloadState.Queued);
        var body = _h.ServeGated("dl_12", DownloadPayloads.Source(3000));
        var page = Page(_h.StartQueue(), ui);
        await ui.RunAsync(() =>
        {
            page.Attach();
            return page.SpaceRead;
        });
        var card = ui.Read(() => Live(page, "sr_1"));
        Assert.Equal("1 of 2 episodes", ui.Read(() => card.Card.Caption));

        body.Release(1500);

        // 4500 of 6000 bytes.
        await Until(() => ui.Read(() => card.Card.Progress) is > 0.74 and < 0.76, "the series' card showed its episodes' progress");
        Assert.Same(card, ui.Read(() => Live(page, "sr_1")));
    }

    [Fact]
    public async Task DeleteAllEpisodes_AsksFirst_InTheAppleAppsWords_AndTakesOnlyThatSeries()
    {
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two");
        await AddEpisodeAsync("dl_a1", 1, 1, "Pilot", seriesTitle: "A Series", seriesId: "sr_2");
        var page = await AttachedAsync();
        _confirmation.Answer = false;

        await PickAsync(page, "sr_1", "Delete All Episodes");

        Assert.Equal(("Delete downloaded files?", "This removes the downloaded copies from this PC. Nothing is deleted from your Silo server.",
            "Delete 2 Downloads"), Assert.Single(_confirmation.Asked));
        Assert.Equal(2, page.Series.Count);

        _confirmation.Answer = true;
        await PickAsync(page, "sr_1", "Delete All Episodes");

        Assert.Null(_h.Find("dl_11"));
        Assert.Null(_h.Find("dl_12"));
        Assert.NotNull(_h.Find("dl_a1"));
        Assert.Equal(["sr_2"], page.Series.Select(c => c.Card.Id));
    }

    [Fact]
    public async Task OneEpisodesSeries_IsAskedAboutInTheSingular()
    {
        await AddEpisodeAsync("dl_11", 1, 1, "Pilot");
        var page = await AttachedAsync();
        _confirmation.Answer = false;

        await PickAsync(page, "sr_1", "Delete All Episodes");

        Assert.Equal("Delete Download", Assert.Single(_confirmation.Asked).Action);
        Assert.Equal("1 episode", Card(page, "sr_1").Caption);
    }

    [Fact]
    public async Task ADownloadAdded_ComesAsACard_AndTheOthersStay()
    {
        await AddFilmAsync("dl_1", "Example Movie");
        var page = await AttachedAsync();
        var first = Live(page, "dl_1");

        await AddFilmAsync("dl_2", "Another Movie");
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");

        Assert.Equal(["dl_2", "dl_1"], page.Films.Select(c => c.Card.Id));
        Assert.Same(first, Live(page, "dl_1"));
        Assert.Equal(["sr_1"], page.Series.Select(c => c.Card.Id));
    }

    [Fact]
    public async Task NoDownloads_SaysHowToGetSome()
    {
        var page = await AttachedAsync();

        Assert.True(page.IsEmpty);
        Assert.False(page.HasFilms);
        Assert.False(page.HasSeries);
        Assert.Equal("No downloads yet", DownloadsViewModel.EmptyTitle);
        Assert.Equal("Choose Download in a film's or episode's ⋯ menu to keep it on this PC and watch it without your Silo server.",
            DownloadsViewModel.EmptyText);
    }

    [Fact]
    public async Task Detach_StopsFollowingTheDownloads()
    {
        var page = await AttachedAsync();

        page.Detach();
        await AddFilmAsync("dl_1", "Example Movie");

        Assert.Empty(Cards(page));
        Assert.True(page.IsEmpty);
        page.Attach();
        Assert.Single(Cards(page));
    }

    [Fact]
    public async Task ADownloadWhoseMediaFileWasDeletedByHand_SaysMissingFile_AndOffersNoPlay()
    {
        var download = await _h.AddPlayableAsync();
        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsWatched = true, SeriesId = null }, Token);
        var queue = _h.StartQueue();
        var page = await AttachedAsync(queue);
        var card = Live(page, "dl_1");
        Assert.Equal(("Watched", "ep_5"), (card.Card.Caption, card.Card.PlayContentId));

        // Deleted in Explorer while Granary runs; the page is left and shown again.
        File.Delete(download.MediaPath!);
        page.Detach();
        page.Attach();

        await Until(() => card.Card.Caption == DownloadText.MissingFile, "the card says the file is missing");
        Assert.Null(card.Card.PlayContentId);
        Assert.True(card.Card.IsCaptionAttention);
        Assert.Equal(["More Info", "Delete download"], Menu(page, "dl_1"));
        // Nothing to free: it is not offered for deletion, and takes no space.
        Assert.False(page.ShowsReclaim);
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
    public async Task AnotherDownloadsFolder_ShowsItsOwnFreeSpace()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 12 * GB);
        _h.Disk.Set(200 * GB);
        var page = await AttachedAsync();
        Assert.Equal("12.0 GB used · 200.0 GB free", page.SpaceText);

        // Settings → Downloads names a folder on another drive while the page stays in memory.
        string other = Path.Combine(_h.Directory, "elsewhere");
        _h.Disk.Set(50 * GB);
        await _h.Preferences.SetFolderAsync(other);
        await page.SpaceRead;

        Assert.Equal("12.0 GB used · 50.0 GB free", page.SpaceText);
        Assert.Equal(other, _h.Disk.Asked[^1]);
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
        Assert.Single(Cards(page));
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
    public async Task ADownloadThatBecomesWatched_IsSuggested_WithItsCardKept()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB);
        var page = await AttachedAsync();
        var card = Live(page, "dl_1");

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsWatched = true }, Token);

        Assert.True(page.ShowsReclaim);
        Assert.Same(card, Live(page, "dl_1"));
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
        Assert.Equal(3, Cards(page).Count());

        _confirmation.Answer = true;
        await page.ReclaimCommand.ExecuteAsync(null);

        Assert.Equal(["dl_2"], Cards(page).Select(c => c.Card.Id));
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
    public async Task ADownloadThatStartedPlayingWhileAnotherWasDeleted_Stays()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_2", "Another Movie", size: GB, change: r => r with { IsWatched = true });
        var page = await AttachedAsync();
        IDisposable? play = null;
        // The question is answered; while the first of the two goes, the viewer starts the other in the minimized player.
        _h.Store.Changed += (_, _) =>
        {
            var left = new[] { "dl_1", "dl_2" }.Where(id => _h.Find(id) is not null).ToList();
            if (left.Count == 1 && play is null)
            {
                play = _h.Playing.Enter(Scope7, left[0]);
            }
        };

        await page.ReclaimCommand.ExecuteAsync(null);

        // Each is looked at right before its delete: the one that plays now stays.
        Assert.NotNull(play);
        Assert.Single(new[] { "dl_1", "dl_2" }, id => _h.Find(id) is not null);
        play.Dispose();
    }

    [Fact]
    public async Task ADownloadTheServerStoppedCountingAsWatchedWhileAnotherWasDeleted_Stays()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_2", "Another Movie", size: GB, change: r => r with { IsWatched = true });
        var downloads = _h.NewManager();
        bool said = false;
        // While the first of the two goes, the read of the server's progress says the other is not watched after all.
        _h.Store.Changed += (_, _) =>
        {
            var left = new[] { "dl_1", "dl_2" }.Where(id => _h.Find(id) is not null).ToList();
            if (left.Count == 1 && !said)
            {
                said = true;
                _h.Store.UpdateAsync(Scope7, left[0], r => r with { IsWatched = false }, Token).GetAwaiter().GetResult();
            }
        };

        int deleted = await downloads.DeleteWatchedAsync(["dl_1", "dl_2"], Token);

        Assert.Equal(1, deleted);
        Assert.Single(new[] { "dl_1", "dl_2" }, id => _h.Find(id) is not null);
    }

    [Fact]
    public async Task AProfileSwitchedToWhileTheWatchedDownloadsGo_DoesNotStopThem_NorLoseCount()
    {
        await AddFilmAsync("dl_1", "Example Movie", size: 2 * GB, change: r => r with { IsWatched = true });
        await AddFilmAsync("dl_2", "Another Movie", size: GB, change: r => r with { IsWatched = true });
        var downloads = _h.NewManager();
        // The answer was about profile 7's downloads; profile 8 becomes the active one while the first goes.
        _h.Store.Changed += (_, _) =>
        {
            if (_h.Session.ProfileId == "7" && (_h.Find("dl_1") is null || _h.Find("dl_2") is null))
            {
                _h.Session.ProfileId = "8";
            }
        };

        int deleted = await downloads.DeleteWatchedAsync(["dl_1", "dl_2"], Token);

        Assert.Equal(2, deleted);
        Assert.Null(_h.Find("dl_1"));
        Assert.Null(_h.Find("dl_2"));
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

    // The app's downloads live as long as the app. The page listens to its view model and the cards, as x:Bind does, so a view
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

    // A download that comes has the page read its speed once a second: the clock lets go of the page with the downloads.
    [Fact]
    public async Task APageThatWasLeft_WhileADownloadCame_IsNotKeptByTheClock()
    {
        await AddFilmAsync("dl_1", "Example Movie", DownloadState.Downloading);
        var downloads = _h.NewManager();
        var (page, spaceRead) = ShowPage(downloads);
        await spaceRead;

        Leave(page);

        Assert.True(await Collected.IsGoneAsync(page));
        GC.KeepAlive(downloads);
    }

    // What the tests above rely on: while the page shows, the downloads do hold it.
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
        page.FollowCards();
        Assert.NotEmpty(Cards(model));
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

    // Stands in for DownloadsPage: it holds its view model and listens to it and to its cards, as the page's bindings do.
    private sealed class ShownPage
    {
        public ShownPage(DownloadsViewModel viewModel)
        {
            ViewModel = viewModel;
            viewModel.PropertyChanged += OnChanged;
        }

        public DownloadsViewModel ViewModel { get; }

        public int Changes { get; private set; }

        public void FollowCards()
        {
            foreach (var card in Cards(ViewModel))
            {
                card.PropertyChanged += OnChanged;
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
}
