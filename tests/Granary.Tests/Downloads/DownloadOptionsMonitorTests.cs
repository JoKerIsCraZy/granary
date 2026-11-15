using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The series download dialog's One time | Monitor, as Silo's Apple app's series sheet (<c>SeriesDownloadSheet.swift</c>): Monitor
/// shows while the server monitors series, starts with the Monitoring Defaults, saves a new monitor or what changed in one, and
/// stops one after asking.
/// </summary>
public sealed class DownloadOptionsMonitorTests : IAsyncDisposable
{
    private const long GB = 1L << 30;
    private const string MonitorsPath = "api/v2/downloads/subscriptions";
    private const string Monitor1Path = "api/v2/downloads/subscriptions/sub_1";
    private const string SyncPath = "api/v2/downloads/subscriptions/sync";

    private static readonly SeriesDownloadRequest Series = new("sr_1", "Example Series",
        [new SeriesSeason(1, [new("ep_1", 1, "Chapter One")]), new SeriesSeason(2, [new("ep_9", 1, "Chapter Nine")])], DownloadQualities.Original);

    private readonly DownloadHarness _h = new();
    private readonly MonitorBook _book = new();
    private SeriesMonitors? _monitors;

    public DownloadOptionsMonitorTests()
    {
        _h.List();
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null)));
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync()));
    }

    public async ValueTask DisposeAsync()
    {
        _monitors?.Dispose();
        await _h.DisposeAsync();
    }

    private SeriesMonitors Monitors => _monitors ??= new SeriesMonitors(new DownloadSubscriptionsApi(_h.Silo.Http), _h.Api, _h.Store, _book,
        _h.NewManager(), _h.Session, _h.Time, _h.Logs.For<SeriesMonitors>());

    // The dialog of the whole series over what the server offers, with the monitors as a run read them.
    private async Task<DownloadOptionsViewModel> SeriesDialogAsync(string capability, params string[] monitors)
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, capability));
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, monitors)));
        await Monitors.SyncAsync(Token);
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        var options = new DownloadOptionsViewModel(_h.NewManager(feature: feature), feature, _h.Preferences, _h.Disk, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadOptionsViewModel>(), Monitors);
        options.ForReading(_ => Task.FromResult<DownloadOffer>(new SeriesDownloadOffer(Series, 2 * GB)), new SeriesMonitorTarget("sr_1", "Example Series"));
        await options.Reading;
        return options;
    }

    private static void ChooseMonitor(DownloadOptionsViewModel options) =>
        options.SelectedKind = options.Kinds.Single(k => k.Label == DownloadOptionsViewModel.MonitorLabel);

    [Fact]
    public async Task ASeriesDialog_OffersOneTimeAndMonitor_OnlyWhileTheServerMonitorsSeries()
    {
        var options = await SeriesDialogAsync(MonitorPayloads.Capability());

        Assert.True(options.ShowsKinds);
        Assert.Equal(["One time", "Monitor"], options.Kinds.Select(k => k.Label));
        Assert.Equal("One time", options.SelectedKind?.Label);
        Assert.Equal("Download now. Nothing else downloads later.", options.KindHint);
        Assert.Equal("Download", options.PrimaryText);

        ChooseMonitor(options);

        Assert.Equal("Keep new episodes coming to this PC as they air.", options.KindHint);
        Assert.Equal("Start monitoring", options.PrimaryText);
        Assert.Equal("Download: Example Series", options.DialogTitle);
        Assert.True(options.CanStart);

        var without = await SeriesDialogAsync(MonitorPayloads.Capability(monitoring: false));
        Assert.False(without.ShowsKinds);
    }

    [Fact]
    public async Task InGerman_MonitorSpeaksTheAppleAppsWords_WithThisPC()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability());

        Assert.Equal(("Download: Example Series · Alle Folgen", "Herunterladen"), (options.DialogTitle, options.PrimaryText));
        Assert.Equal(["Einmalig", "Überwachen"], options.Kinds.Select(k => k.Label));

        ChooseMonitor(options);

        Assert.Equal("Neue Folgen kommen nach der Ausstrahlung automatisch auf diesen PC.", options.KindHint);
        Assert.Equal(("Download: Example Series", "Überwachung starten"), (options.DialogTitle, options.PrimaryText));
        Assert.Equal(["Keins", "10 GB", "25 GB", "50 GB", "100 GB"], options.StorageLimits.Select(l => l.Label));
        Assert.Equal("Überwachte Folgen werden in Originalqualität heruntergeladen.", options.MonitorSummary);
        Assert.Equal("Überwachung von Example Series beenden?", options.StopTitle);
    }

    [Fact]
    public async Task Monitor_StartsOnFutureEpisodes_WithTheMonitoringDefaults_AndOnlyTheModesTheServerOffers()
    {
        await _h.Preferences.SetDefaultDeleteWatchedAsync(true);
        await _h.Preferences.SetDefaultMonitorLimitGbAsync(25);

        var options = await SeriesDialogAsync(MonitorPayloads.Capability(modes: ["future", "latest_season"]));

        Assert.Equal([("Future episodes", "Only episodes that air from now on"), ("Last season", "Season 2 and anything newer")],
            options.MonitorRows.Select(r => (r.Title, r.Detail)));
        Assert.Equal(SubscriptionModes.Future, options.SelectedRule?.Mode);
        Assert.True(options.DeleteWatched);
        Assert.Equal(["None", "10 GB", "25 GB", "50 GB", "100 GB"], options.StorageLimits.Select(l => l.Label));
        Assert.Equal("25 GB", options.SelectedStorageLimit?.Label);
    }

    [Fact]
    public async Task StartMonitoring_CreatesTheMonitorWithTheChoices_AndCloses()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(mode: "all", quality: "10mbps", maxStorageBytes: 50 * GB)));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability());
        ChooseMonitor(options);
        options.SelectedRule = options.MonitorRows.Single(r => r.Mode == SubscriptionModes.All);
        options.SelectedMonitorQuality = options.MonitorQualities.Single(q => q.Value == "10mbps");
        options.SelectedStorageLimit = options.StorageLimits.Single(l => l.Label == "50 GB");

        Assert.Equal("Monitored episodes download at 10 Mbps.", options.MonitorSummary);
        Assert.True(await options.StartAsync());

        var create = Assert.Single(_h.Silo.SentTo(HttpMethod.Post, MonitorsPath));
        Assert.Equal("""{"series_id":"sr_1","mode":"all","delete_watched":false,"max_storage_bytes":53687091200,"quality":"10mbps"}""", create.Body);
        Assert.Equal("sub_1", Monitors.For("sr_1")?.Id);
    }

    // The dialog holds Cancel and Esc while IsStarting (DownloadOptionsDialog.HoldWhileStarting): a save under way counts as a
    // start, so the dialog stays until it answered and shows that answer. Cancel then only stops a save still waiting.
    [Fact]
    public async Task AMonitorBeingSaved_CountsAsAStart_UntilItsAnswerCame()
    {
        var (held, gate) = ItemDetailFixture.Held();
        _h.Silo.On(HttpMethod.Post, MonitorsPath, held);
        var options = await SeriesDialogAsync(MonitorPayloads.Capability());
        ChooseMonitor(options);

        var start = options.StartAsync();
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, MonitorsPath).Count == 1, "the monitor's create was sent");

        Assert.True(options.IsStarting);
        Assert.False(options.CanStart);
        options.StopStarting();
        gate.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor())());

        Assert.True(await start);
        Assert.False(options.IsStarting);
        Assert.Equal("sub_1", Monitors.For("sr_1")?.Id);
    }

    [Fact]
    public async Task AMonitoredSeries_OpensOnMonitor_WithItsOwnChoices_AndSavesOnlyWhatChanged()
    {
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", deleteWatched: true, maxStorageBytes: 0)));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(), MonitorPayloads.Monitor(deleteWatched: true, maxStorageBytes: 10 * GB));

        Assert.Equal(("Monitor", "Save"), (options.SelectedKind?.Label, options.PrimaryText));
        Assert.True(options.HasMonitor);
        Assert.True(options.DeleteWatched);
        Assert.Equal("10 GB", options.SelectedStorageLimit?.Label);
        // Unchanged, Save only closes.
        Assert.True(await options.StartAsync());
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));

        options.SelectedStorageLimit = options.StorageLimits.Single(l => l.Label == "None");
        Assert.True(await options.StartAsync());

        var patch = Assert.Single(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
        Assert.Equal(("\"m1\"", """{"delete_watched":true,"max_storage_bytes":0,"active":true}"""), (patch.IfMatch, patch.Body));
    }

    [Fact]
    public async Task ASaveOverAMonitorThatChangedElsewhere_ShowsItAsItIsNow_AndSaysSo()
    {
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m9\"", mode: "latest_season", deleteWatched: true)));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(), MonitorPayloads.Monitor());
        options.DeleteWatched = true;

        Assert.False(await options.StartAsync());

        Assert.Equal("Monitoring for this series changed on the server. Try again.", options.ErrorText);
        Assert.Equal(SubscriptionModes.LatestSeason, options.SelectedRule?.Mode);
        Assert.True(options.DeleteWatched);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
    }

    [Fact]
    public async Task ASaveOverAMonitorARunReadAnewWhileTheDialogShowed_SendsNothing_AndShowsItAsItIsNow()
    {
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m3\"")));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(), MonitorPayloads.Monitor());
        // Another client turned Delete after watching on; a run read it while the dialog showed the monitor as it was.
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor(etag: "\"m2\"", deleteWatched: true))));
        await Monitors.SyncAsync(Token);
        options.SelectedStorageLimit = options.StorageLimits.Single(l => l.Label == "10 GB");

        Assert.False(await options.StartAsync());

        Assert.Empty(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
        Assert.Equal(DownloadOptionsViewModel.MonitorChanged, options.ErrorText);
        Assert.True(options.DeleteWatched);
        Assert.Equal("None", options.SelectedStorageLimit?.Label);
    }

    [Fact]
    public async Task StopMonitoring_AsksFirst_ThenStops_AndCloses()
    {
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NoContent());
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(), MonitorPayloads.Monitor());
        int closes = 0;
        options.CloseRequested += (_, _) => closes++;

        options.StopMonitoringCommand.Execute(null);

        Assert.True(options.IsConfirmingStop);
        Assert.Equal("Stop monitoring Example Series?", options.StopTitle);
        Assert.False(options.CanStart);
        options.KeepMonitoringCommand.Execute(null);
        Assert.False(options.IsConfirmingStop);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Delete, Monitor1Path));

        options.StopMonitoringCommand.Execute(null);
        await options.ConfirmStopCommand.ExecuteAsync(null);

        Assert.Equal("\"m1\"", Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, Monitor1Path)).IfMatch);
        Assert.Null(Monitors.For("sr_1"));
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task WithoutMonitorQuality_MonitoredEpisodesComeInOriginal_AndNoQualityIsSent()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(quality: null)));
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(monitorQuality: false));
        ChooseMonitor(options);

        Assert.False(options.ShowsMonitorQualities);
        Assert.Equal("Monitored episodes download in original quality.", options.MonitorSummary);
        Assert.True(await options.StartAsync());
        Assert.DoesNotContain("quality", Assert.Single(_h.Silo.SentTo(HttpMethod.Post, MonitorsPath)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherClientsCustomMonitor_KeepsItsSeasons()
    {
        var options = await SeriesDialogAsync(MonitorPayloads.Capability(), MonitorPayloads.Monitor(mode: "specific_seasons", seasons: [2, 3]));

        var custom = Assert.Single(options.MonitorRows, r => r.Mode == SubscriptionModes.SpecificSeasons);
        Assert.Equal(("Custom", "Seasons 2 and 3"), (custom.Title, custom.Detail));
        Assert.Same(custom, options.SelectedRule);
    }
}
