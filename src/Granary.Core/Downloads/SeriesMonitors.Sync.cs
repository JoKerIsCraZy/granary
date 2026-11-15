using System.Globalization;
using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>A run: the monitor list, each active monitor's sync, and the episodes it registered.</summary>
public sealed partial class SeriesMonitors
{
    /// <summary>The waits before a sync page that got no answer is sent again, once each (the Apple app's one and two seconds).</summary>
    public static readonly IReadOnlyList<TimeSpan> PageRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    // A sync starts over at most this often after its monitor changed under it (409), as the Apple app's.
    private const int SyncRestarts = 2;

    // The most pages one sync, or a registry read, goes through.
    private const int MaxPages = 1000;

    // The statuses of a registry entry worth taking: still to come, or on the server whole.
    private static readonly HashSet<string> Takeable = new(StringComparer.Ordinal)
    {
        DownloadStatuses.Preparing, DownloadStatuses.Ready, DownloadStatuses.Downloading, DownloadStatuses.Completed,
    };

    // Under _one.
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Target() is not var (scope, server) || _store.IsClearPending(scope))
        {
            return;
        }
        DownloadCapability capability;
        try
        {
            capability = await _downloads.GetCapabilityAsync(server, scope.ProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("Whether the server monitors series could not be read ({Failure})", Describe(ex));
            return;
        }
        if (!capability.IsUsable || !IsStill(scope))
        {
            return;
        }
        await SendStopsAsync(scope, server, cancellationToken).ConfigureAwait(false);
        await ReadListAsync(scope, server, cancellationToken).ConfigureAwait(false);
        if (capability.CanMonitorSeries)
        {
            foreach (var monitor in _book.In(scope).Where(m => m.Active))
            {
                if (!IsStill(scope) || await SyncOneAsync(scope, server, monitor, cancellationToken).ConfigureAwait(false) == SyncEnd.StopRun)
                {
                    break;
                }
            }
        }
        if (_book.In(scope).Count > 0 && IsStill(scope))
        {
            await TakeRegisteredAsync(scope, server, cancellationToken).ConfigureAwait(false);
        }
    }

    // The whole list, or nothing from it. Monitors whose stop waits to be sent stay out: the server lists them until it goes.
    private async Task<bool> ReadListAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadSubscription> listed;
        try
        {
            listed = await _api.ListAsync(server, scope.ProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The monitored series could not be read ({Failure})", Describe(ex));
            return false;
        }
        if (!IsStill(scope))
        {
            return false;
        }
        var stopping = StopsOf(scope);
        _book.Replace(scope, [.. listed.Where(m => !stopping.ContainsKey(m.Id))]);
        return true;
    }

    private enum SyncEnd
    {
        Done,

        /// <summary>The server could not be reached, or asks to wait: the other monitors would meet the same.</summary>
        StopRun,
    }

    // One monitor page by page under the validator read before its first page, as the Apple app's syncDownloadSubscription.
    private async Task<SyncEnd> SyncOneAsync(DownloadScope scope, Uri server, DownloadSubscription monitor, CancellationToken cancellationToken)
    {
        string etag = monitor.Etag;
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        int restarts = 0;
        int retries = 0;
        int pages = 0;
        while (pages < MaxPages)
        {
            try
            {
                var page = await _api.SyncPageAsync(server, scope.ProfileId, monitor.Id, etag, cursor, cancellationToken).ConfigureAwait(false);
                pages++;
                retries = 0;
                if (page.Page is not { HasMore: true })
                {
                    return SyncEnd.Done;
                }
                if (page.Page.NextCursor is not { Length: > 0 } next || !cursors.Add(next))
                {
                    _logger.LogWarning("A monitored series' sync did not end whole");
                    return SyncEnd.Done;
                }
                cursor = next;
            }
            catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
            {
                // The monitor is gone, or its series is no longer the profile's: only a read tells which.
                await ReadAgainAsync(scope, server, monitor.Id, cancellationToken).ConfigureAwait(false);
                return SyncEnd.Done;
            }
            catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.Conflict && restarts < SyncRestarts)
            {
                restarts++;
                if (await ReadAgainAsync(scope, server, monitor.Id, cancellationToken).ConfigureAwait(false) is not { Active: true } reread)
                {
                    return SyncEnd.Done;
                }
                etag = reread.Etag;
                cursor = null;
                cursors.Clear();
                pages = 0;
                retries = 0;
            }
            catch (Exception ex) when (IsUnanswered(ex, cancellationToken) && retries < PageRetryDelays.Count)
            {
                await Task.Delay(PageRetryDelays[retries], _time, cancellationToken).ConfigureAwait(false);
                retries++;
            }
            catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
            {
                _logger.LogInformation("A monitored series could not be synced ({Failure})", Describe(ex));
                return IsNotReached(ex) ? SyncEnd.StopRun : SyncEnd.Done;
            }
            if (!IsStill(scope))
            {
                return SyncEnd.StopRun;
            }
        }
        _logger.LogWarning("A monitored series' sync did not end within {Pages} pages", MaxPages);
        return SyncEnd.Done;
    }

    // The monitor as the server has it now, kept; one it no longer has leaves the list. Null when it is gone or could not be read.
    private async Task<DownloadSubscription?> ReadAgainAsync(DownloadScope scope, Uri server, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var reread = await _api.GetAsync(server, scope.ProfileId, subscriptionId, cancellationToken).ConfigureAwait(false);
            if (IsStill(scope))
            {
                _book.Put(scope, reread);
            }
            return reread;
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            if (IsStill(scope))
            {
                _logger.LogInformation("A monitored series is no longer monitored on the server");
                _book.Remove(scope, subscriptionId);
            }
            return null;
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("A monitored series could not be read again ({Failure})", Describe(ex));
            return null;
        }
    }

    // The registry's entries of a monitored series that this PC neither keeps nor is deleting, with their manifests, kept as
    // downloads in episode order, so the queue, which takes the oldest first, brings the first episode first. A sign-out of the
    // account under way, or since the run began, keeps them off this PC.
    private async Task TakeRegisteredAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        long clears = _store.ClearGeneration;
        if (_store.IsClearPending(scope))
        {
            return;
        }
        var series = _book.In(scope).Select(m => m.SeriesId).ToHashSet(StringComparer.Ordinal);
        var entries = await ListRegistryAsync(scope, server, cancellationToken).ConfigureAwait(false);
        if (entries is null || !IsStill(scope))
        {
            return;
        }
        var deleting = _store.PendingDeletes(scope).Select(d => d.DownloadId).ToHashSet(StringComparer.Ordinal);
        var taken = new List<DownloadRecord>();
        foreach (var entry in entries.Where(e => e.EpisodeId is { Length: > 0 } && series.Contains(e.ContentId) && Takeable.Contains(e.Status)))
        {
            if (_store.Find(scope, entry.Id) is not null || deleting.Contains(entry.Id) || _store.FindByItem(scope, entry.EpisodeId!) is not null
                || taken.Any(t => t.MediaItemId == entry.EpisodeId))
            {
                continue;
            }
            if (await RecordOfAsync(scope, server, entry, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                break;
            }
            taken.Add(record);
        }
        var now = _time.GetUtcNow();
        int order = 0;
        foreach (var record in taken.OrderBy(r => r.SeasonNumber is 0 ? 1 : 0).ThenBy(r => r.SeasonNumber ?? int.MaxValue)
            .ThenBy(r => r.EpisodeNumber ?? int.MaxValue))
        {
            if (!IsStill(scope))
            {
                return;
            }
            await KeepAsync(record with { CreatedAt = now.AddTicks(order++) }, clears).ConfigureAwait(false);
        }
        if (taken.Count > 0)
        {
            _logger.LogInformation("{Count} episodes of monitored series are kept to download", taken.Count);
        }
    }

    // Every page of the registry; null when it could not be read whole.
    private async Task<List<DownloadEntry>?> ListRegistryAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        var entries = new List<DownloadEntry>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (int page = 0; page < MaxPages; page++)
            {
                var read = await _downloads.ListAsync(server, scope.ProfileId, cursor, cancellationToken).ConfigureAwait(false);
                entries.AddRange(read.Items);
                if (read.Page is not { HasMore: true })
                {
                    return entries;
                }
                if (read.Page.NextCursor is not { Length: > 0 } next || !cursors.Add(next))
                {
                    break;
                }
                cursor = next;
            }
            // What was read is the server's all the same: entries are only ever taken, never judged gone, from it.
            return entries;
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The download registry could not be read for the monitored series ({Failure})", Describe(ex));
            return null;
        }
    }

    // The entry as a download, its title and numbers from its manifest; null when the manifest does not come, which ends this
    // run's taking: the next run tries again.
    private async Task<DownloadRecord?> RecordOfAsync(DownloadScope scope, Uri server, DownloadEntry entry, CancellationToken cancellationToken)
    {
        OfflineManifest manifest;
        try
        {
            manifest = OfflineManifest.Read(await _downloads.GetManifestAsync(server, scope.ProfileId, entry.Id, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The manifest of download {DownloadId} could not be read ({Failure}); it is taken with the next run", entry.Id, Describe(ex));
            return null;
        }
        string title = !string.IsNullOrWhiteSpace(manifest.Title) ? manifest.Title.Trim()
            : manifest.EpisodeNumber is { } number ? "Episode " + number.ToString(CultureInfo.InvariantCulture)
            : "Episode";
        string? seriesTitle = !string.IsNullOrWhiteSpace(manifest.SeriesTitle) ? manifest.SeriesTitle.Trim() : _book.TitleOf(scope, entry.ContentId);
        return new DownloadRecord
        {
            DownloadId = entry.Id,
            Scope = scope,
            MediaItemId = entry.EpisodeId!,
            SeriesId = entry.ContentId,
            Title = title,
            SeriesTitle = seriesTitle,
            SeasonNumber = manifest.SeasonNumber,
            EpisodeNumber = manifest.EpisodeNumber,
            MediaFileId = entry.MediaFileId ?? manifest.MediaFileId,
            Quality = string.IsNullOrEmpty(entry.Quality) ? DownloadQualities.Original : entry.Quality,
            Revision = entry.Revision,
            ExpectedBytes = Math.Max(0, entry.FileSize),
            State = entry.Status == DownloadStatuses.Preparing ? DownloadState.Preparing : DownloadState.Queued,
            PreparationProgress = entry.Preparation is { Progress: >= 0 and <= 1 } preparation ? preparation.Progress : null,
        };
    }

    private async Task KeepAsync(DownloadRecord record, long clears)
    {
        try
        {
            if (await _store.AddUnlessClearedAsync(record, clears, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                _book.NoteTitle(record.Scope, record.SeriesId!, record.SeriesTitle);
            }
        }
        catch (InvalidOperationException)
        {
            // Kept meanwhile, as by a Download pressed for the same episode.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server's entry stays: the next run takes it.
            _logger.LogWarning("Download {DownloadId} of a monitored series could not be kept on this PC ({Failure})", record.DownloadId,
                DownloadLog.Failure(ex));
        }
    }

    // A failure of a request, not a bug: an answer, no answer, an answer that is no Silo answer, a profile switch or a timeout.
    private static bool IsServerFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    // Sent, and no answer came that says what became of it: a connection that broke off, a timeout or a server error.
    private static bool IsUnanswered(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException http => http.HttpRequestError is not (HttpRequestError.InvalidResponse or HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError),
        SiloApiException api => api.Status >= 500 && api.Status != (int)HttpStatusCode.ServiceUnavailable,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    // The server could not be reached, or asks to wait: whatever comes next would meet the same.
    private static bool IsNotReached(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: not HttpRequestError.InvalidResponse } or ProfileChangedException
        || ex is SiloApiException { Status: (int)HttpStatusCode.TooManyRequests or (int)HttpStatusCode.ServiceUnavailable };

    private static string Describe(Exception ex) => ex switch
    {
        SiloApiException api => api.Status.ToString(CultureInfo.InvariantCulture),
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => ex.GetType().Name,
    };
}
