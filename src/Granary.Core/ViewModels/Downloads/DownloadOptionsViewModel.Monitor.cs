using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>The series a download dialog can monitor: its id, and its title as the page or the card names it.</summary>
public sealed record SeriesMonitorTarget(string SeriesId, string SeriesTitle);

/// <summary>A rule the dialog's Monitor offers: its mode, its name and the line under it.</summary>
public sealed record MonitorRuleRow(string Mode, string Title, string Detail);

/// <summary>
/// A series' download dialog's One Time | Monitor, as Silo's Apple app's series sheet (<c>SeriesDownloadSheet.swift</c>) has
/// them, in its words with "this PC" for its device: One Time downloads the series now; Monitor keeps new episodes coming, by a
/// rule (All Episodes, Future Episodes by default, Last Season; another client's Custom kept, never offered), in a quality when
/// the server keeps one for monitors, with Delete after watching and a storage limit, which start from Settings → Downloads'
/// Monitoring Defaults. Monitor shows while the server monitors series, and for a series monitored already so it can be
/// stopped, which asks first. Saving sends only what changed; a monitor that changed on the server meanwhile is shown as it is
/// now, and saying so is all.
/// </summary>
public sealed partial class DownloadOptionsViewModel
{
    public const string OneTimeLabel = "One Time";
    public const string MonitorLabel = "Monitor";
    public const string OneTimeHint = "Download now. Nothing else downloads later.";
    public const string MonitorHint = "Keep new episodes coming to this PC as they air.";
    public const string StartMonitoringText = "Start Monitoring";
    public const string SaveText = "Save";
    public const string StopMonitoringText = "Stop Monitoring";
    public const string StopMessage = "Episodes already on this PC stay until you delete them.";
    public const string StopAction = "Stop";
    public const string KeepAction = "Keep";
    public const string NoLimit = "None";
    public const string SaveFailed = "Couldn't Save Monitoring";
    public const string MonitorChanged = "Monitoring for this series changed on the server. Try again.";
    public const string MonitorRemoved = "This series is no longer monitored.";

    /// <summary>The Apple app's words, with Granary for the app that could not confirm it.</summary>
    public const string MonitoringUncertain = "Granary couldn't confirm the monitoring change. Check this series again once you're back online.";

    // The storage limits a new monitor offers, as the Apple app's menu: none, 10, 25, 50 and 100 GB.
    private static readonly int[] LimitChoicesGb = [0, 10, 25, 50, 100];

    private const string OneTimeKind = "one_time";
    private const string MonitorKind = "monitor";

    private SeriesMonitorTarget? _monitorTarget;

    // The validator of the monitor the dialog shows; null while it shows none. A save goes over this one, or over nothing.
    private string? _shownEtag;

    // The newest regular season of the series as read, which Last Season starts from.
    private int? _latestSeason;

    /// <summary>One Time and, when the series can be monitored, Monitor.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Kinds { get; private set; } = [];

    /// <summary>The series can be monitored here: the choice between One Time and Monitor shows.</summary>
    [ObservableProperty]
    public partial bool ShowsKinds { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonitorChosen), nameof(ShowsOneTime), nameof(PrimaryText), nameof(DialogTitle), nameof(CanStart))]
    public partial SettingChoice? SelectedKind { get; set; }

    /// <summary>Monitor is chosen: the dialog saves a monitor rather than downloading.</summary>
    public bool IsMonitorChosen => SelectedKind?.Value == MonitorKind;

    /// <summary>The download's own parts show: One Time is chosen, or nothing else is offered.</summary>
    public bool ShowsOneTime => !IsMonitorChosen;

    /// <summary>The Apple app's line under the choice.</summary>
    [ObservableProperty]
    public partial string KindHint { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<MonitorRuleRow> MonitorRows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial MonitorRuleRow? SelectedRule { get; set; }

    /// <summary>The presets a monitor can keep, labelled as the qualities are; none on a server whose monitors take originals.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMonitorQualities))]
    public partial IReadOnlyList<SettingChoice> MonitorQualities { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedMonitorQuality { get; set; }

    /// <summary>A monitor's quality can be chosen: the server keeps one, and offers more than Original.</summary>
    public bool ShowsMonitorQualities => MonitorQualities.Count > 1;

    [ObservableProperty]
    public partial bool DeleteWatched { get; set; }

    /// <summary>"None", "10 GB", ... and a monitor's own limit when it is none of them, so it stays as it is unless another is picked.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> StorageLimits { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedStorageLimit { get; set; }

    /// <summary>The series is monitored: Stop Monitoring shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(ShowsStopMonitoring))]
    public partial bool HasMonitor { get; private set; }

    /// <summary>Stop Monitoring was pressed: the dialog asks, with Stop and Keep.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(ShowsStopMonitoring))]
    public partial bool IsConfirmingStop { get; private set; }

    /// <summary>Stop Monitoring shows: the series is monitored, and the question is not up.</summary>
    public bool ShowsStopMonitoring => HasMonitor && !IsConfirmingStop;

    /// <summary>"Stop monitoring Example Series?"</summary>
    public string StopTitle => $"Stop monitoring {_monitorTarget?.SeriesTitle}?";

    /// <summary>"Episode 6 downloads Thursday. Monitored episodes download at 10 Mbps.", the Apple app's one sentence on what happens next.</summary>
    [ObservableProperty]
    public partial string MonitorSummary { get; private set; } = "";

    /// <summary>The dialog's button: "Download"; on Monitor, "Start Monitoring", or "Save" for a monitored series.</summary>
    public string PrimaryText => !IsMonitorChosen ? PlainTitle : HasMonitor && CurrentMonitor()?.Active == true ? SaveText : StartMonitoringText;

    /// <summary>The dialog's title: <see cref="Title"/>, and on Monitor the series', "Download: Example Series".</summary>
    public string DialogTitle => IsMonitorChosen && _monitorTarget is { } target ? $"{PlainTitle}: {target.SeriesTitle}" : Title;

    /// <summary>Raised when the dialog is done without its button: a monitor was stopped.</summary>
    public event EventHandler? CloseRequested;

    // Save can be pressed: a rule the server takes, nothing under way, not while Stop is asked.
    private bool CanSaveMonitor =>
        !IsStarting && !IsConfirmingStop && SelectedRule is { } rule && _monitors is not null
        && ((_feature.Capability?.CanMonitorSeries == true && MonitorRows.Contains(rule)) || CurrentMonitor() is not null);

    // Monitor's choices for the series, from its monitor or the Monitoring Defaults; Monitor first when asked for, or when the
    // series is monitored and running, as the Apple app opens.
    private void SetUpMonitor(SeriesMonitorTarget? target, bool startsOnMonitor)
    {
        _monitorTarget = target;
        var existing = CurrentMonitor();
        bool canMonitor = target is not null && _monitors is not null
            && ((_feature.Capability?.CanMonitorSeries == true && RuleRows(null).Count > 0) || existing is not null);
        ShowsKinds = canMonitor;
        var oneTime = new SettingChoice(OneTimeKind, OneTimeLabel);
        Kinds = canMonitor ? [oneTime, new SettingChoice(MonitorKind, MonitorLabel)] : [oneTime];
        if (!canMonitor)
        {
            SelectedKind = oneTime;
            return;
        }
        Prefill(existing);
        SelectedKind = startsOnMonitor || existing?.Active == true ? Kinds[1] : oneTime;
    }

    // The series' offer was read: Last Season names the season it starts from.
    private void NoteSeasons(SeriesDownloadRequest series)
    {
        _latestSeason = series.Seasons.Where(s => s.SeasonNumber > 0).Select(s => (int?)s.SeasonNumber).Max();
        if (_monitorTarget is not null && MonitorRows.Count > 0)
        {
            // A list's choice is one of its own rows: cleared first, since a row of the old list equals its new one.
            string? chosen = SelectedRule?.Mode;
            SelectedRule = null;
            MonitorRows = RuleRows(CurrentMonitor());
            SelectedRule = MonitorRows.FirstOrDefault(r => r.Mode == chosen) ?? MonitorRows.FirstOrDefault();
        }
    }

    private void Prefill(DownloadSubscription? existing)
    {
        SelectedRule = null;
        MonitorRows = RuleRows(existing);
        SelectedRule = MonitorRows.FirstOrDefault(r => r.Mode == (existing?.Mode ?? SubscriptionModes.Future)) ?? MonitorRows.FirstOrDefault();
        var capability = _feature.Capability;
        if (capability?.MonitorQuality == true)
        {
            var offered = OfferedQualities();
            string? stored = existing is null ? null : existing.Quality ?? DownloadQualities.Original;
            if (stored is not null && offered.All(o => o.Preset != stored))
            {
                offered.Add(new DownloadQualityOption(stored, null, null));
            }
            SelectedMonitorQuality = null;
            MonitorQualities = [.. offered.Select(o => new SettingChoice(o.Preset, DownloadText.QualityLabel(o)))];
            string preferred = offered.Any(o => o.Preset == _preferences.DefaultQuality) ? _preferences.DefaultQuality : DownloadQualities.Original;
            SelectedMonitorQuality = MonitorQualities.FirstOrDefault(q => q.Value == (stored ?? preferred)) ?? MonitorQualities.FirstOrDefault();
        }
        else
        {
            MonitorQualities = [];
            SelectedMonitorQuality = null;
        }
        DeleteWatched = existing?.DeleteWatched ?? _preferences.DefaultDeleteWatched;
        long limit = existing?.MaxStorageBytes ?? _preferences.DefaultMonitorLimitGb * DownloadPreferenceLimits.BytesPerGb;
        var limits = LimitChoicesGb.Select(gb => gb * DownloadPreferenceLimits.BytesPerGb).ToList();
        if (!limits.Contains(limit))
        {
            limits.Add(limit);
            limits.Sort();
        }
        SelectedStorageLimit = null;
        StorageLimits = [.. limits.Select(bytes => new SettingChoice(Bytes(bytes), bytes == 0 ? NoLimit : MonitorRules.LimitText(bytes)))];
        SelectedStorageLimit = StorageLimits.FirstOrDefault(l => l.Value == Bytes(limit));
        HasMonitor = existing is not null;
        _shownEtag = existing?.Etag;
        ShowMonitorSummary();
    }

    // The rows the server offers (all, when it lists none), in the Apple app's order; a monitor's own rule stays listed when the
    // server no longer offers it, and another client's specific seasons show as Custom.
    private List<MonitorRuleRow> RuleRows(DownloadSubscription? existing)
    {
        var advertised = _feature.Capability?.MonitoringModes is { Count: > 0 } modes ? modes : SubscriptionModes.Known;
        var rows = new[] { SubscriptionModes.All, SubscriptionModes.Future, SubscriptionModes.LatestSeason }
            .Where(mode => advertised.Contains(mode) || mode == existing?.Mode)
            .Select(mode => new MonitorRuleRow(mode, MonitorRules.ModeName(mode), MonitorRules.ModeDetail(mode, LatestSeason(existing, mode), [])))
            .ToList();
        if (existing?.Mode == SubscriptionModes.SpecificSeasons)
        {
            rows.Add(new MonitorRuleRow(SubscriptionModes.SpecificSeasons, MonitorRules.Custom,
                MonitorRules.ModeDetail(SubscriptionModes.SpecificSeasons, null, existing.SeasonNumbers)));
        }
        return rows;
    }

    // The season Last Season starts from: the monitor's own when it has that rule, else the newest regular season.
    private int? LatestSeason(DownloadSubscription? existing, string mode) =>
        mode == SubscriptionModes.LatestSeason && existing?.Mode == SubscriptionModes.LatestSeason && existing.TargetSeason is { } target
            ? target
            : _latestSeason;

    partial void OnSelectedKindChanged(SettingChoice? value)
    {
        KindHint = IsMonitorChosen ? MonitorHint : _monitorTarget is not null ? OneTimeHint : "";
        ErrorText = "";
        IsConfirmingStop = false;
    }

    partial void OnSelectedRuleChanged(MonitorRuleRow? value) => ShowMonitorSummary();

    partial void OnSelectedMonitorQualityChanged(SettingChoice? value) => ShowMonitorSummary();

    // "Monitored episodes download at 10 Mbps.", after the next airing the rule takes when the calendar knows one.
    private void ShowMonitorSummary()
    {
        string quality = ShowsMonitorQualities && SelectedMonitorQuality is { } chosen ? chosen.Value : DownloadQualities.Original;
        string sentence = quality == DownloadQualities.Original
            ? "Monitored episodes download in original quality."
            : $"Monitored episodes download at {DownloadText.QualityLabel(new DownloadQualityOption(quality, null, null))}.";
        if (_schedule is { } schedule && _monitorTarget is { } target && SelectedRule is { } rule)
        {
            var existing = CurrentMonitor();
            var known = _downloads.Items.Where(i => i.SeriesId == target.SeriesId && i.State != DownloadState.Failed).Select(i => i.MediaItemId).ToList();
            var now = _time.GetUtcNow();
            if (MonitorRules.NextEpisode(rule.Mode, LatestSeason(existing, rule.Mode), existing?.SeasonNumbers ?? [], schedule.Upcoming(target.SeriesId), known, now) is { } next)
            {
                sentence = MonitorRules.Headline(new MonitorStatus.Next(next), now, _time.LocalTimeZone) + ". " + sentence;
            }
        }
        MonitorSummary = sentence;
    }

    /// <summary>Stop Monitoring: the dialog asks first, in the Apple app's words.</summary>
    [RelayCommand]
    private void StopMonitoring()
    {
        if (!IsStarting && CurrentMonitor() is not null)
        {
            ErrorText = "";
            IsConfirmingStop = true;
        }
    }

    /// <summary>Keep: the question goes, and nothing changes.</summary>
    [RelayCommand]
    private void KeepMonitoring() => IsConfirmingStop = false;

    /// <summary>
    /// Stop: the monitor stops (its downloads stay), and the dialog closes. Cancel while it waits for a run under way stops the
    /// wait, and the dialog closes with the monitor as it was.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmStopAsync()
    {
        IsConfirmingStop = false;
        // One thing at a time: a save under way owns the dialog's Cancel.
        if (IsStarting || _monitors is null || CurrentMonitor() is not { } monitor)
        {
            return;
        }
        IsStarting = true;
        using var stopping = new CancellationTokenSource();
        _stopping = stopping;
        try
        {
            await _monitors.StopAsync(monitor.Id, stopping.Token);
        }
        catch (Exception ex)
        {
            // A command's failure would end the app: the dialog closes either way, and the list says what holds.
            if (ex is not OperationCanceledException)
            {
                _logger.LogWarning("A monitor could not be stopped ({Failure})", DownloadLog.Failure(ex));
            }
        }
        finally
        {
            _stopping = null;
            await _dispatcher.ResumeOnUi();
            IsStarting = false;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // Saves Monitor's choices: a new monitor, or what changed in the series' own; one unchanged and running only closes. True
    // once it was saved, false with why on the dialog. The viewer's Cancel while it waits for a run under way
    // (<paramref name="stopping"/>) ends it as stopped, true, with nothing sent; a save that began is never cut off.
    private async Task<bool> SaveMonitorAsync(CancellationToken stopping)
    {
        if (_monitors is null || _monitorTarget is not { } target || SelectedRule is not { } rule || !CanSaveMonitor)
        {
            return false;
        }
        var existing = CurrentMonitor();
        if (existing?.Etag != _shownEtag)
        {
            // A run read the monitor anew while the dialog showed it (or one came): shown as it is now, nothing sent.
            Prefill(existing);
            ErrorText = MonitorChanged;
            return false;
        }
        long limit = long.TryParse(SelectedStorageLimit?.Value, out long bytes) ? bytes : 0;
        // Only to a server that keeps a monitor's quality; without it, monitors take originals.
        string? quality = _feature.Capability?.MonitorQuality == true ? SelectedMonitorQuality?.Value : null;
        string? storedQuality = existing is null ? null : existing.Quality ?? DownloadQualities.Original;
        if (existing is { Active: true } && existing.Mode == rule.Mode && existing.DeleteWatched == DeleteWatched && existing.MaxStorageBytes == limit
            && (quality is null || quality == storedQuality))
        {
            return true;
        }
        IsStarting = true;
        ErrorText = "";
        MonitorResult result;
        try
        {
            result = existing is null
                ? await _monitors.CreateAsync(target.SeriesId, target.SeriesTitle, new MonitorChoice(rule.Mode, DeleteWatched, limit, quality), stopping)
                : await _monitors.UpdateAsync(existing.Id, new MonitorEdit(rule.Mode == existing.Mode ? null : rule.Mode, DeleteWatched, limit,
                    quality == storedQuality ? null : quality) { Etag = _shownEtag }, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The viewer closed the dialog while it waited: nothing was sent.
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("A monitor could not be saved ({Failure})", DownloadLog.Failure(ex));
            result = MonitorResult.Failed;
        }
        finally
        {
            await _dispatcher.ResumeOnUi();
            IsStarting = false;
        }
        switch (result)
        {
            case MonitorResult.Saved:
                return true;
            case MonitorResult.Changed or MonitorResult.Removed:
                // Shown as the server has it now: the viewer saves again over what they see.
                Prefill(CurrentMonitor());
                ErrorText = result == MonitorResult.Changed ? MonitorChanged : MonitorRemoved;
                return false;
            case MonitorResult.Uncertain:
                ErrorText = MonitoringUncertain;
                return false;
            default:
                ErrorText = SaveFailed;
                return false;
        }
    }

    private DownloadSubscription? CurrentMonitor() => _monitorTarget is { } target ? _monitors?.For(target.SeriesId) : null;

    private static string Bytes(long bytes) => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
