using System.Globalization;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Starting downloads. A create is never sent twice (the server's guide, 9.3): a second press while one runs, or for a title
/// the PC keeps, creates nothing, and an answer that did not come is looked up in the registry instead of asked again.
/// </summary>
public sealed partial class DownloadManager
{
    // The most registry pages an uncertain create looks through.
    private const int MaxPages = 1000;

    /// <summary>
    /// Creates the server's entry for a film or an episode and keeps it, queued, or preparing while the server prepares a copy.
    /// A film's <c>content_id</c> is its own id; an episode's is its series, with the episode as <c>episode_id</c>.
    /// </summary>
    public async Task<DownloadStartResult> StartAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DownloadScope.Of(_session) is not { } scope || _session.ActiveServer is not { } server)
        {
            return DownloadStartResult.Unavailable;
        }
        var key = (scope, request.MediaItemId);
        lock (_gate)
        {
            if (!_starting.Add(key))
            {
                return DownloadStartResult.AlreadyDownloading;
            }
        }
        try
        {
            await _store.LoadAsync(cancellationToken);
            if (_store.FindByItem(scope, request.MediaItemId) is not null)
            {
                return DownloadStartResult.AlreadyDownloading;
            }
            var (entry, result) = await CreateAsync(server.BaseUri, scope, BodyOf(request), cancellationToken);
            return entry is null ? result : await KeepAsync(scope, request, entry);
        }
        finally
        {
            lock (_gate)
            {
                _starting.Remove(key);
            }
        }
    }

    /// <summary>A season's episodes one create each, in order, passing over the ones this PC has or is fetching.</summary>
    public async Task<SeasonStartResult> StartSeasonAsync(SeasonDownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int started = 0;
        int there = 0;
        int failed = 0;
        foreach (var episode in request.Episodes)
        {
            var result = await StartAsync(new DownloadRequest(episode.EpisodeId, request.SeriesId, null, request.Quality, episode.Title, request.SeriesTitle,
                request.SeasonNumber, episode.EpisodeNumber), cancellationToken);
            switch (result)
            {
                case DownloadStartResult.Started:
                    started++;
                    break;
                case DownloadStartResult.AlreadyDownloading:
                    there++;
                    break;
                default:
                    failed++;
                    break;
            }
        }
        return new SeasonStartResult(started, there, failed);
    }

    private CreateDownloadBody BodyOf(DownloadRequest request) => new()
    {
        ContentId = request.SeriesId ?? request.MediaItemId,
        EpisodeId = request.SeriesId is null ? null : request.MediaItemId,
        MediaFileId = request.MediaFileId,
        Quality = request.Quality,
        Caps = DownloadCaps.For(_probe.Facts, _device.DecodesInSoftware),
        ExpectedRevision = 0,
    };

    // The entry the server made, or why there is none. A refusal is final; an answer that did not come, a conflict or a server
    // error may have made one, so the registry tells.
    private async Task<(DownloadEntry? Entry, DownloadStartResult Result)> CreateAsync(Uri server, DownloadScope scope, CreateDownloadBody body,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _api.CreateAsync(server, scope.ProfileId, body, cancellationToken);
            if ((created.Items.FirstOrDefault(e => IsFor(e, body)) ?? created.Items.FirstOrDefault()) is { } entry)
            {
                return (entry, DownloadStartResult.Started);
            }
            _logger.LogInformation("The server passed the download over ({Reason})", created.Skipped.FirstOrDefault()?.Reason ?? "no reason");
            return (null, DownloadStartResult.Unavailable);
        }
        catch (SiloApiException ex) when (ex.Status is 403 or 404 or 501)
        {
            _logger.LogInformation("The server does not let this profile download it ({Status})", ex.Status);
            return (null, DownloadStartResult.Unavailable);
        }
        catch (Exception ex) when (IsUncertain(ex, cancellationToken))
        {
            _logger.LogInformation("A download's create got no clear answer ({Failure}); the registry is read", Describe(ex));
            return await AdoptAsync(server, scope, body, cancellationToken) is { } entry
                ? (entry, DownloadStartResult.Started)
                : (null, DownloadStartResult.Failed);
        }
        catch (Exception ex) when (ex is SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("A download could not be created ({Failure})", Describe(ex));
            return (null, DownloadStartResult.Failed);
        }
    }

    // No answer, a conflict (an entry is there already) or a server error: the server may have made the entry.
    private static bool IsUncertain(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SiloApiException { Status: 409 or >= 500 }
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    // The newest entry of the registry for this film or episode that is not over and not kept here; null when there is none or
    // the registry cannot be read.
    private async Task<DownloadEntry?> AdoptAsync(Uri server, DownloadScope scope, CreateDownloadBody body, CancellationToken cancellationToken)
    {
        string? cursor = null;
        try
        {
            for (int page = 0; page < MaxPages; page++)
            {
                var read = await _api.ListAsync(server, scope.ProfileId, cursor, cancellationToken);
                if (read.Items.FirstOrDefault(e => IsFor(e, body) && IsLive(e) && _store.Find(scope, e.Id) is null) is { } entry)
                {
                    _logger.LogInformation("The server had made download {DownloadId}; it is kept", entry.Id);
                    return entry;
                }
                if (read.Page is not { HasMore: true, NextCursor: { Length: > 0 } next })
                {
                    return null;
                }
                cursor = next;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("The download registry could not be read ({Failure})", Describe(ex));
            return null;
        }
    }

    private static bool IsFor(DownloadEntry entry, CreateDownloadBody body) => entry.ContentId == body.ContentId && entry.EpisodeId == body.EpisodeId;

    private static bool IsLive(DownloadEntry entry) =>
        entry.Status is not (DownloadStatuses.Revoked or DownloadStatuses.Failed or DownloadStatuses.Cancelled);

    // The entry kept with the request's title fields, and the queue told. Once the server made it, a cancel does not stop it
    // from being kept: a create is not sent again.
    private async Task<DownloadStartResult> KeepAsync(DownloadScope scope, DownloadRequest request, DownloadEntry entry)
    {
        var record = new DownloadRecord
        {
            DownloadId = entry.Id,
            Scope = scope,
            MediaItemId = request.MediaItemId,
            SeriesId = request.SeriesId,
            Title = request.Title,
            SeriesTitle = request.SeriesTitle,
            SeasonNumber = request.SeasonNumber,
            EpisodeNumber = request.EpisodeNumber,
            MediaFileId = entry.MediaFileId ?? request.MediaFileId,
            Quality = request.Quality,
            Revision = entry.Revision,
            ExpectedBytes = Math.Max(0, entry.FileSize),
            State = entry.Status == DownloadStatuses.Preparing ? DownloadState.Preparing : DownloadState.Queued,
            PreparationProgress = entry.Preparation is { Progress: >= 0 and <= 1 } preparation ? preparation.Progress : null,
            CreatedAt = _time.GetUtcNow(),
        };
        try
        {
            await _store.AddAsync(record, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return DownloadStartResult.AlreadyDownloading;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server's entry stays: the next create finds it in the registry.
            _logger.LogWarning("Download {DownloadId} could not be kept on this PC ({Failure})", entry.Id, DownloadLog.Failure(ex));
            return DownloadStartResult.Failed;
        }
        _logger.LogInformation("Download {DownloadId} is kept, {State}", entry.Id, record.State);
        _queue.Wake();
        return DownloadStartResult.Started;
    }

    private static string Describe(Exception ex) => ex switch
    {
        SiloApiException api => api.Status.ToString(CultureInfo.InvariantCulture),
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => ex.GetType().Name,
    };
}
