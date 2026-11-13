using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// A series' downloads, opened from its card on the Downloads page: its name, how many and their size, the episodes' stills by
/// season with Specials last, the cards' menus as on the Downloads page, and back to it once nothing of the series is left.
/// </summary>
public sealed class DownloadedSeriesViewModelTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeConfirmation _confirmation = new();

    public DownloadedSeriesViewModelTests()
    {
        // As the app gets there: the Downloads page, then the series' card.
        _navigation.ShowSection(AppRoute.Downloads);
        _navigation.NavigateTo(AppRoute.DownloadedSeries, "sr_1");
        _navigation.Navigations.Clear();
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadedSeriesViewModel Page(DownloadManager? downloads = null) =>
        new(downloads ?? _h.NewManager(), _navigation, _confirmation, new ImmediateDispatcher());

    private DownloadedSeriesViewModel Shown(string seriesId = "sr_1")
    {
        var page = Page();
        page.Open(seriesId);
        page.Attach();
        return page;
    }

    private Task<StoredDownload> AddEpisodeAsync(string id, int? season, int? episode, string title, string seriesTitle = "Example Series",
        string seriesId = "sr_1", DownloadState state = DownloadState.Completed, Func<DownloadRecord, DownloadRecord>? change = null) =>
        _h.AddAsync(id, state, mediaItemId: "ep_" + id, change: r =>
        {
            var episodeRecord = r with { Title = title, SeriesId = seriesId, SeriesTitle = seriesTitle, SeasonNumber = season, EpisodeNumber = episode };
            return change is null ? episodeRecord : change(episodeRecord);
        });

    private static IEnumerable<LiveCard> Cards(DownloadedSeriesViewModel page) => page.Seasons.SelectMany(s => s.Episodes);

    private static MediaCard Card(DownloadedSeriesViewModel page, string id) => Cards(page).Single(c => c.Card.Id == id).Card;

    private static async Task PickAsync(DownloadedSeriesViewModel page, string id, string label)
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
    public async Task TheEpisodes_ShowBySeason_SpecialsLast_EachByNumber()
    {
        await AddEpisodeAsync("dl_s1", 0, 1, "Behind the Scenes");
        await AddEpisodeAsync("dl_25", 2, 5, "Second Chapter Five");
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        await AddEpisodeAsync("dl_12", 1, 2, "Chapter Two");
        await AddEpisodeAsync("dl_a1", 1, 1, "Pilot", seriesTitle: "A Series", seriesId: "sr_2");

        var page = Shown();

        Assert.Equal("Example Series", page.Title);
        // Four of 3000 bytes each.
        Assert.Equal("4 episodes · 11.7 KB", page.Summary);
        Assert.Equal(["Season 1", "Season 2", "Specials"], page.Seasons.Select(s => s.Title));
        Assert.Equal(["S01E02 · Chapter Two", "S01E05 · Chapter Five"], page.Seasons[0].Episodes.Select(c => c.Card.Title));
        Assert.Equal(["S00E01 · Behind the Scenes"], page.Seasons[2].Episodes.Select(c => c.Card.Title));
    }

    [Fact]
    public async Task AnEpisode_IsItsStillFromThisPc_WithItsSizeAndStatus()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", change: r => r with
        {
            ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg", ["series_poster"] = "series_poster.jpg" },
        });
        await AddEpisodeAsync("dl_16", 1, 6, "Chapter Six", state: DownloadState.Queued);

        var page = Shown();

        var whole = Card(page, "dl_15");
        Assert.Equal((MediaCardShape.Landscape, Path.Combine(_h.Find("dl_15")!.Folder, "poster.jpg"), "2.9 KB", "Downloaded", "ep_dl_15"),
            (whole.Shape, whole.LocalImagePath, whole.Subtitle, whole.Caption, whole.PlayContentId));
        Assert.Equal("Queued", Card(page, "dl_16").Caption);
        Assert.Equal("1 of 2 episodes · 5.9 KB", page.Summary);
    }

    [Fact]
    public async Task AnEpisodesCard_PlaysOnceWhole_AndItsMenuIsTheDownloadsPages()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        await AddEpisodeAsync("dl_16", 1, 6, "Chapter Six", state: DownloadState.Downloading);
        var page = Shown();

        page.OpenCardCommand.Execute(Card(page, "dl_15"));
        await PickAsync(page, "dl_16", "Pause");

        Assert.Equal([(AppRoute.Player, (object?)PlayRequest.Resume("ep_dl_15"))], _navigation.Navigations);
        Assert.True(_h.Find("dl_16")!.Record.IsPausedByViewer);
        Assert.Equal("Paused", Card(page, "dl_16").Caption);
    }

    [Fact]
    public async Task ACardThatChanges_StaysInPlace()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five", state: DownloadState.Downloading);
        var page = Shown();
        var season = page.Seasons[0];
        var card = season.Episodes[0];

        await _h.Store.UpdateAsync(Scope7, "dl_15", r => r with { State = DownloadState.Completed }, Token);

        Assert.Same(season, page.Seasons[0]);
        Assert.Same(card, season.Episodes[0]);
        Assert.Equal("Downloaded", card.Card.Caption);
    }

    [Fact]
    public async Task TheLastEpisodeDeleted_GoesBackToTheDownloads()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        await AddEpisodeAsync("dl_16", 1, 6, "Chapter Six");
        var page = Shown();

        await PickAsync(page, "dl_15", "Delete download");
        Assert.Equal(0, _navigation.BackCount);
        Assert.Equal(["S01E06 · Chapter Six"], Cards(page).Select(c => c.Card.Title));

        await PickAsync(page, "dl_16", "Delete download");

        Assert.Empty(page.Seasons);
        Assert.Equal(1, _navigation.BackCount);
        Assert.Equal(AppRoute.Downloads, _navigation.Current);
    }

    [Fact]
    public async Task AnotherSeries_StartsFromNothing()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        await AddEpisodeAsync("dl_a1", 1, 1, "Pilot", seriesTitle: "A Series", seriesId: "sr_2");
        var page = Shown();
        var first = Cards(page).Single();
        page.Detach();

        page.Open("sr_2");
        page.Attach();

        Assert.Equal("A Series", page.Title);
        Assert.Equal(["dl_a1"], Cards(page).Select(c => c.Card.Id));
        Assert.DoesNotContain(first, Cards(page));
    }

    [Fact]
    public async Task APageThatWasLeft_IsNotKeptByTheDownloads()
    {
        await AddEpisodeAsync("dl_15", 1, 5, "Chapter Five");
        var downloads = _h.NewManager();
        var page = ShowPage(downloads);

        Leave(page);

        Assert.True(await Collected.IsGoneAsync(page));
        GC.KeepAlive(downloads);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference<DownloadedSeriesViewModel> ShowPage(DownloadManager downloads)
    {
        var page = Page(downloads);
        page.Open("sr_1");
        page.Attach();
        Assert.NotEmpty(Cards(page));
        return new WeakReference<DownloadedSeriesViewModel>(page);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Leave(WeakReference<DownloadedSeriesViewModel> page)
    {
        if (page.TryGetTarget(out var shown))
        {
            shown.Detach();
        }
    }
}
