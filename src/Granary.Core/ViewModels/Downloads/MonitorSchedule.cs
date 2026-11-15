using System.Globalization;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>A monitored series' title and poster, as its page has them.</summary>
public sealed record MonitoredSeriesInfo(string Title, Uri? Poster, string? PosterThumbhash);

/// <summary>
/// What the monitor screens need beyond the monitors, as Silo's Apple app's <c>AutoDownloadSchedule</c> reads it: the episodes
/// that air in the next month (<c>GET /api/v2/calendar</c>, every airing, in the viewer's zone) by series, and each monitored
/// series' title and poster (<c>GET /api/v2/catalog/items/{id}</c>). Read again at most every <see cref="RefreshInterval"/>,
/// unless asked to, and only while something is monitored; held for one server, account and profile, and dropped for
/// another. Its reads go only as the account and profile they began for, so another profile's airings and titles are never
/// kept for this one. <see cref="Changed"/> comes on any thread once something new was read.
/// </summary>
public sealed class MonitorSchedule
{
    /// <summary>How long what was read counts as current, as the Apple app's.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    // The calendar answers at most 31 days at once.
    private const int WindowDays = CalendarApi.MaxDays;

    private readonly CalendarApi _calendar;
    private readonly CatalogApi _catalog;
    private readonly ISessionManager _session;
    private readonly SeriesMonitors _monitors;
    private readonly TimeProvider _time;
    private readonly ILogger<MonitorSchedule> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);

    private readonly Lock _gate = new();
    private DownloadScope? _scope;
    private DateTimeOffset? _loadedAt;
    private Dictionary<string, List<UpcomingEpisode>> _upcoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MonitoredSeriesInfo> _info = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _infoFailedAt = new(StringComparer.Ordinal);

    public MonitorSchedule(CalendarApi calendar, CatalogApi catalog, ISessionManager session, SeriesMonitors monitors, TimeProvider time,
        ILogger<MonitorSchedule> logger)
    {
        _calendar = calendar;
        _catalog = catalog;
        _session = session;
        _monitors = monitors;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised on any thread once the calendar or a series' title or poster was read.</summary>
    public event EventHandler? Changed;

    /// <summary>The calendar was read for the active profile: an empty one means nothing airs, not "not known".</summary>
    public bool IsKnown
    {
        get
        {
            lock (_gate)
            {
                return _scope == DownloadScope.Of(_session) && _loadedAt is not null;
            }
        }
    }

    /// <summary>The series' airings still to come in the next month, for the active profile.</summary>
    public IReadOnlyList<UpcomingEpisode> Upcoming(string seriesId)
    {
        lock (_gate)
        {
            return _scope == DownloadScope.Of(_session) && _upcoming.TryGetValue(seriesId, out var list) ? [.. list] : [];
        }
    }

    /// <summary>The series' title and poster, for the active profile; null until read.</summary>
    public MonitoredSeriesInfo? Info(string seriesId)
    {
        lock (_gate)
        {
            return _scope == DownloadScope.Of(_session) ? _info.GetValueOrDefault(seriesId) : null;
        }
    }

    /// <summary>
    /// Reads the calendar when it is older than <see cref="RefreshInterval"/>, or always with <paramref name="force"/>, and the
    /// titles and posters not read yet; nothing while nothing is monitored. A read that fails leaves what was read before.
    /// </summary>
    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(_session) is not { } scope || _session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        // The series of the profile the read is for, taken with its scope: another profile's monitors are never asked about here.
        string[] series = [.. _monitors.Monitors.Select(m => m.SeriesId).Distinct(StringComparer.Ordinal)];
        if (series.Length == 0 || DownloadScope.Of(_session) != scope)
        {
            return;
        }
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DownloadScope.Of(_session) != scope)
            {
                // Another profile acts since this refresh began: what was read for that one stays, and this asks nothing.
                return;
            }
            var now = _time.GetUtcNow();
            bool due;
            lock (_gate)
            {
                if (_scope != scope)
                {
                    _scope = scope;
                    _loadedAt = null;
                    _upcoming = new Dictionary<string, List<UpcomingEpisode>>(StringComparer.Ordinal);
                    _info.Clear();
                    _infoFailedAt.Clear();
                }
                due = force || _loadedAt is not { } loaded || now - loaded >= RefreshInterval;
            }
            bool changed = false;
            try
            {
                changed = due && await ReadCalendarAsync(scope, server, now, cancellationToken).ConfigureAwait(false);
                changed |= await ReadInfoAsync(scope, server, series, now, cancellationToken).ConfigureAwait(false);
            }
            catch (ProfileChangedException)
            {
                // Another profile or account acts now: the read was not sent, nor is the rest. Nothing failed, so the next
                // refresh for this profile asks again.
                _logger.LogDebug("The monitored series' schedule was not read: another profile acts now");
            }
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// The episode airings still to come, by series, as the Apple app reads them: an airing with its exact time counts until
    /// then; one dated by its day alone from the next day on, since the calendar cannot say whether today's has aired. A season
    /// premiere stands for its first episode.
    /// </summary>
    public static Dictionary<string, List<UpcomingEpisode>> UpcomingOf(CalendarWeek week, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(week);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var bySeries = new Dictionary<string, List<UpcomingEpisode>>(StringComparer.Ordinal);
        foreach (var item in week.Events.SelectMany(day => day.Items))
        {
            if (item.Type is not ("episode" or "season_premiere") || item.SeriesId?.Trim() is not { Length: > 0 } seriesId
                || item.SeasonNumber is not { } season || (item.EpisodeNumber ?? (item.Type == "season_premiere" ? 1 : null)) is not { } episode)
            {
                continue;
            }
            DateTimeOffset airDate;
            if (item.AirAt is { } exact)
            {
                if (exact < now)
                {
                    continue;
                }
                airDate = exact;
            }
            else if (DateOnly.TryParseExact(item.LocalAirDate ?? item.AirDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day > today)
            {
                var midnight = day.ToDateTime(TimeOnly.MinValue);
                airDate = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
            }
            else
            {
                continue;
            }
            if (!bySeries.TryGetValue(seriesId, out var list))
            {
                bySeries[seriesId] = list = [];
            }
            list.Add(new UpcomingEpisode(item.ContentId, seriesId, season, episode, item.EpisodeTitle, airDate));
        }
        return bySeries;
    }

    private async Task<bool> ReadCalendarAsync(DownloadScope scope, Uri server, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zone = _time.LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        try
        {
            var week = await _calendar.GetAsAccountProfileAsync(server, scope.AccountId, scope.ProfileId, today, today.AddDays(WindowDays),
                CalendarFilters.Everything, CalendarText.IanaZone(zone, now), cancellationToken).ConfigureAwait(false);
            var upcoming = UpcomingOf(week, now, zone);
            lock (_gate)
            {
                if (_scope != scope)
                {
                    return false;
                }
                _upcoming = upcoming;
                _loadedAt = now;
            }
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The airing calendar could not be read for the monitored series ({Failure})", ex.GetType().Name);
            return false;
        }
    }

    // The titles and posters of monitored series not read yet, one after another; one that failed is not asked again before the
    // refresh interval passed.
    private async Task<bool> ReadInfoAsync(DownloadScope scope, Uri server, IReadOnlyList<string> series, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (string seriesId in series)
        {
            lock (_gate)
            {
                if (_scope != scope || _info.ContainsKey(seriesId)
                    || (_infoFailedAt.TryGetValue(seriesId, out var failed) && now - failed < RefreshInterval))
                {
                    continue;
                }
            }
            try
            {
                var item = await _catalog.GetItemAsAccountProfileAsync(server, scope.AccountId, scope.ProfileId, seriesId, cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    if (_scope == scope)
                    {
                        _info[seriesId] = new MonitoredSeriesInfo(item.Title, ArtworkUrl.Resolve(server, item.PosterUrl), item.PosterThumbhash);
                        _infoFailedAt.Remove(seriesId);
                        changed = true;
                    }
                }
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("A monitored series' title could not be read ({Failure})", ex.GetType().Name);
                lock (_gate)
                {
                    _infoFailedAt[seriesId] = now;
                }
            }
        }
        return changed;
    }
}
