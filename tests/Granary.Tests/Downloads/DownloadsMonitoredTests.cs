using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Downloads;
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
/// The Downloads page's Monitored series, as Silo's Apple app's Monitored list (<c>AutoDownloadsView.swift</c>): each as a card
/// with its rule and what it does next, running ones first; a card opens its series' dialog on Monitor.
/// </summary>
public sealed class DownloadsMonitoredTests : IAsyncDisposable
{
    private const string MonitorsPath = "api/v2/downloads/subscriptions";
    private const string SyncPath = "api/v2/downloads/subscriptions/sync";

    private const string SecondSeries = """
        { "content_id": "sr_2", "type": "series", "title": "Another Series", "status": "matched", "genres": [], "keywords": [],
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "versions": [], "videos": [], "subtitles": [] }
        """;

    // Episode 6 of Example Series airs on the Sunday after the clock's Thursday.
    private const string Calendar = """
        { "events": [ { "date": "2026-10-04", "items": [
            { "content_id": "ep_6", "type": "episode", "title": "Example Series", "series_id": "sr_1", "season_number": 1,
              "episode_number": 6, "air_at": "2026-10-04T20:00:00Z" } ] } ] }
        """;

    private readonly DownloadHarness _h = new();
    private readonly MonitorBook _book = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeDownloadOptionsDialog _dialog = new();
    private SeriesMonitors? _monitors;

    public DownloadsMonitoredTests()
    {
        _h.List();
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability()))
            .On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync()))
            .On(HttpMethod.Get, ItemDetailFixture.ItemPath("sr_1"), ItemDetailFixture.Ok(TitlePayloads.Series))
            .On(HttpMethod.Get, ItemDetailFixture.ItemPath("sr_2"), ItemDetailFixture.Ok(SecondSeries))
            .On(HttpMethod.Get, ItemDetailFixture.SeasonsPath("sr_1"), ItemDetailFixture.Ok("""{ "items": [] }"""))
            .On(HttpMethod.Get, "api/v2/calendar", ScriptedSilo.Json(HttpStatusCode.OK, Calendar));
    }

    public async ValueTask DisposeAsync()
    {
        _monitors?.Dispose();
        await _h.DisposeAsync();
    }

    private async Task<DownloadsViewModel> PageAsync(params string[] monitors)
    {
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, monitors)));
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        var manager = _h.NewManager(feature: feature);
        _monitors = new SeriesMonitors(new DownloadSubscriptionsApi(_h.Silo.Http), _h.Api, _h.Store, _book, manager, _h.Session, _h.Time,
            _h.Logs.For<SeriesMonitors>());
        await _monitors.SyncAsync(Token);
        var schedule = new MonitorSchedule(new CalendarApi(_h.Silo.Http), new CatalogApi(_h.Silo.Http), _h.Session, _monitors, _h.Time,
            _h.Logs.For<MonitorSchedule>());
        var ui = new ImmediateDispatcher();
        var actions = new DownloadActions(manager, feature, _dialog, new FakeConfirmation(), _navigation, ui,
            () => new DownloadOptionsViewModel(manager, feature, _h.Preferences, _h.Disk, ui, _h.Time, _h.Logs.For<DownloadOptionsViewModel>(), _monitors, schedule),
            _h.Logs.For<DownloadActions>());
        var page = new DownloadsViewModel(manager, _h.Disk, _h.Preferences, _navigation, new FakeConfirmation(), ui, _h.Time, _h.Logs.For<DownloadsViewModel>(),
            monitors: _monitors, schedule: schedule, downloadActions: actions,
            offers: new DownloadOfferReader(new CatalogApi(_h.Silo.Http), _h.Session, manager));
        page.Attach();
        await page.ScheduleRead;
        return page;
    }

    private static MediaCard Card(DownloadsViewModel page, string seriesId) => page.Monitored.Single(c => c.Card.Id == seriesId).Card;

    [Fact]
    public async Task EachMonitoredSeries_IsACard_WithItsRuleAndWhatComesNext_RunningOnesFirst()
    {
        var page = await PageAsync(MonitorPayloads.Monitor("sub_2", "sr_2", etag: "\"m2\"", active: false),
            MonitorPayloads.Monitor("sub_1", "sr_1", quality: "10mbps"));

        Assert.True(page.HasMonitored);
        Assert.Equal(["Example Series", "Another Series"], page.Monitored.Select(c => c.Card.Title));
        var running = Card(page, "sr_1");
        Assert.Equal(("Future episodes · 10 Mbps", "Episode 6 on Sunday"), (running.Subtitle, running.Caption));
        Assert.Equal(DownloadCards.MonitorType, running.Type);
        Assert.Equal("Paused", Card(page, "sr_2").Caption);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task AnEpisodeOnItsWay_SaysWhereItStands()
    {
        await _h.AddAsync("dl_7", DownloadState.Queued, mediaItemId: "ep_7",
            change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series", SeasonNumber = 1, EpisodeNumber = 7 });

        var page = await PageAsync(MonitorPayloads.Monitor());

        Assert.Equal("Episode 7 is next", Card(page, "sr_1").Caption);
    }

    [Fact]
    public async Task AMonitorsCard_OpensItsSeriesDialogOnMonitor_AndItsMenuGoesToTheSeries()
    {
        var page = await PageAsync(MonitorPayloads.Monitor());
        var card = Card(page, "sr_1");

        page.OpenCardCommand.Execute(card);
        await Until(() => _dialog.Shown.Count == 1, "the dialog opened");

        var shown = _dialog.Shown[0];
        Assert.True(shown.IsMonitorChosen);
        Assert.Equal("Download: Example Series", shown.DialogTitle);

        var entries = page.Actions.EntriesFor(card);
        Assert.Equal(["Go to series", "Options"], entries.Select(e => e.Label));
        entries[0].Command.Execute(card);
        Assert.Equal((AppRoute.Item, (object?)"sr_1"), _navigation.Navigations[^1]);
    }
}
