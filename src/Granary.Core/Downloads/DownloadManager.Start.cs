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
    /// A film's <c>content_id</c> is its own id; an episode's is its series, with the episode as <c>episode_id</c>. An entry
    /// that comes after a sign-out took the account's downloads off this PC is not kept (<see cref="DownloadStartResult.Failed"/>).
    /// While the account is being signed out, nothing is created (<see cref="DownloadStartResult.Unavailable"/>).
    /// </summary>
    public async Task<DownloadStartResult> StartAsync(DownloadRequest request, CancellationToken cancellationToken) =>
        (await StartAsync(request, null, onlyWhileWaiting: false, cancellationToken).ConfigureAwait(false)).Result;

    // As the public StartAsync, for the active profile; with onlyFor, only while that one is the active profile (a season's run
    // and the waiting line are their profile's), else Unavailable with nothing sent. With onlyWhileWaiting, a waiting episode's
    // create, which goes, and keeps its download, only while the episode is still in line. Answers also whether a failure is
    // for good, and the wait a refusal for the limit names.
    private async Task<CreateOutcome> StartAsync(DownloadRequest request, DownloadScope? onlyFor, bool onlyWhileWaiting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Taken before the scope and the sign-out check: a sign-out from here on keeps whatever this create makes off this PC.
        long clears = _store.ClearGeneration;
        if (DownloadScope.Of(_session) is not { } scope || _session.ActiveServer is not { } server || (onlyFor is not null && scope != onlyFor))
        {
            return new(DownloadStartResult.Unavailable);
        }
        if (_store.IsClearPending(scope))
        {
            // Its download would be kept after the sign-out took the account's along, and stay behind it.
            _logger.LogInformation("A download is not started while its account is being signed out");
            return new(DownloadStartResult.Unavailable);
        }
        var key = (scope, request.MediaItemId);
        lock (_gate)
        {
            if (!_starting.Add(key))
            {
                return new(DownloadStartResult.AlreadyDownloading);
            }
        }
        try
        {
            await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_store.FindByItem(scope, request.MediaItemId) is not null)
            {
                return new(DownloadStartResult.AlreadyDownloading);
            }
            // A waiting episode's create goes only while it is still in line: the viewer may have taken it out meanwhile.
            if (onlyWhileWaiting && !_store.IsWaiting(scope, request.MediaItemId))
            {
                return new(DownloadStartResult.Unavailable, Dropped: true);
            }
            var (entry, outcome) = await CreateAsync(server.BaseUri, scope, BodyOf(request), cancellationToken).ConfigureAwait(false);
            return entry is null ? outcome : await KeepAsync(scope, request, entry, clears, onlyWhileWaiting).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _starting.Remove(key);
            }
        }
    }

    /// <summary>
    /// A season's episodes one create each, in order, passing over the ones this PC has, is fetching or keeps waiting. It stops,
    /// with what it counted so far, when it is cancelled or the profile it started for is no longer the active one: the episodes
    /// after that are in none of the counts. Once the server's limit of downloads refuses one
    /// (<see cref="DownloadStartResult.Limited"/>), that one and the ones after it wait on this PC (<see cref="WaitingEpisode"/>)
    /// and are asked for again once the server allows more (DownloadManager.Waiting.cs); the result counts them as
    /// <see cref="SeasonStartResult.Waiting"/>. A cancel stops it between two episodes: the create under way is not cut off,
    /// since one the server made is kept (a create is never sent again).
    /// </summary>
    public Task<SeasonStartResult> StartSeasonAsync(SeasonDownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartEpisodesAsync(request.SeriesId, request.SeriesTitle, request.Quality,
            request.Episodes.Select(e => (request.SeasonNumber, e)), cancellationToken);
    }

    /// <summary>
    /// A whole series as its seasons one after the other, each as <see cref="StartSeasonAsync"/> starts one: one create per
    /// episode, the same stops. Silo's apps ask the server for a series in batches; Granary asks per episode, as it does for a
    /// season, so each episode gets the file the dialog's size counted and the create's guards (docs/api-contract.md, 14.2).
    /// </summary>
    public Task<SeasonStartResult> StartSeriesAsync(SeriesDownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartEpisodesAsync(request.SeriesId, request.SeriesTitle, request.Quality,
            request.Seasons.SelectMany(s => s.Episodes.Select(e => (s.SeasonNumber, e))), cancellationToken);
    }

    private async Task<SeasonStartResult> StartEpisodesAsync(string seriesId, string seriesTitle, string quality,
        IEnumerable<(int Season, SeasonEpisode Episode)> episodes, CancellationToken cancellationToken)
    {
        // Taken before the first create: a sign-out from here on keeps nothing of this run waiting.
        long clears = _store.ClearGeneration;
        var scope = DownloadScope.Of(_session);
        var all = episodes.ToList();
        int started = 0;
        int there = 0;
        int failed = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var (season, episode) = all[i];
            if (cancellationToken.IsCancellationRequested || scope is null || DownloadScope.Of(_session) != scope)
            {
                _logger.LogInformation("A season's or series' downloads stopped after {Count} episodes", started + there + failed);
                break;
            }
            // One waiting for the server's limit already is on its way, as one on this PC is.
            if (_store.IsWaiting(scope, episode.EpisodeId))
            {
                there++;
                continue;
            }
            // Taken before the create: a place the server frees while it is asked is not lost to the refusal (Waiting.cs).
            long freed = FreeGeneration;
            // For the profile the run started for, which the check above looked at: another one active by the create sends nothing.
            var outcome = await StartAsync(new DownloadRequest(episode.EpisodeId, seriesId, episode.MediaFileId, quality, episode.Title,
                seriesTitle, season, episode.EpisodeNumber), scope, onlyWhileWaiting: false, CancellationToken.None).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case DownloadStartResult.Started:
                    started++;
                    break;
                case DownloadStartResult.AlreadyDownloading:
                    there++;
                    break;
                case DownloadStartResult.Limited when cancellationToken.IsCancellationRequested:
                    // The viewer stopped the run while that create was under way: nothing of it waits to download later.
                    _logger.LogInformation("A season's or series' downloads stopped after {Count} episodes", started + there + failed + 1);
                    return new SeasonStartResult(started, there, failed + 1, ReachedLimit: true);
                case DownloadStartResult.Limited:
                    int waiting = await KeepWaitingAsync(scope, seriesId, seriesTitle, quality, all.Skip(i), clears, outcome.RetryAfter, freed)
                        .ConfigureAwait(false);
                    _logger.LogInformation("The server's download limit stopped a season's or series' downloads after {Count} episodes; {Waiting} wait",
                        started + there + failed, waiting);
                    return new SeasonStartResult(started, there, waiting == 0 ? failed + 1 : failed, ReachedLimit: true, waiting);
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
    // error may have made one, so the registry tells. A conflict over an entry that ended (revoked, failed, cancelled) revives it
    // with one explicit second create, never more.
    private async Task<(DownloadEntry? Entry, CreateOutcome Outcome)> CreateAsync(Uri server, DownloadScope scope, CreateDownloadBody body,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _api.CreateAsync(server, scope.AccountId, scope.ProfileId, body, cancellationToken).ConfigureAwait(false);
            return EntryOf(created, body);
        }
        catch (SiloApiException ex) when (ex.Status is 403 or 404 or 501)
        {
            _logger.LogInformation("The server does not let this profile download it ({Status})", ex.Status);
            return (null, new(DownloadStartResult.Unavailable));
        }
        catch (SiloApiException ex) when (IsLimit(ex))
        {
            // Refused before anything was made: the account's limit of downloads, which holds for the next create too.
            _logger.LogInformation("The server's download limit for the account was reached");
            return (null, new(DownloadStartResult.Limited, RetryAfter: ex.RetryAfter));
        }
        catch (Exception ex) when (IsUncertain(ex, cancellationToken))
        {
            _logger.LogInformation("A download's create got no clear answer ({Failure}); the registry is read", Describe(ex));
            var (live, ended) = await FindInRegistryAsync(server, scope, body, cancellationToken).ConfigureAwait(false);
            if (live is not null)
            {
                return (live, new(DownloadStartResult.Started));
            }
            // Only a conflict says the server refused because of that entry; after no answer, the next press asks anew.
            if (ended is not null && ex is SiloApiException { Status: 409 })
            {
                return await ReviveAsync(server, scope, body, ended, cancellationToken).ConfigureAwait(false);
            }
            return (null, new(DownloadStartResult.Failed));
        }
        catch (Exception ex) when (ex is SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("A download could not be created ({Failure})", Describe(ex));
            return (null, new(DownloadStartResult.Failed, IsFinal(ex)));
        }
    }

    // The title's entry in a create's answer; an answer for another title keeps nothing, an empty one passed the title over.
    private (DownloadEntry? Entry, CreateOutcome Outcome) EntryOf(DownloadCreated created, CreateDownloadBody body)
    {
        if (created.Items.FirstOrDefault(e => IsFor(e, body)) is { } entry)
        {
            return (entry, new(DownloadStartResult.Started));
        }
        if (created.Items.Count > 0)
        {
            _logger.LogWarning("The server answered a download's create for another title");
            return (null, new(DownloadStartResult.Failed, IsFinal: true));
        }
        _logger.LogInformation("The server passed the download over ({Reason})", created.Skipped.FirstOrDefault()?.Reason ?? "no reason");
        return (null, new(DownloadStartResult.Unavailable));
    }

    // The second and last create for a title whose entry ended: it names that entry and the revision the registry gave it, so
    // the server reuses, revives or replaces exactly that one (the guide's 4.1). Whatever it answers, nothing is sent again.
    private async Task<(DownloadEntry? Entry, CreateOutcome Outcome)> ReviveAsync(Uri server, DownloadScope scope, CreateDownloadBody body,
        DownloadEntry ended, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Download {DownloadId} ended on the server ({Status}); it is asked for again", ended.Id, ended.Status);
        try
        {
            var created = await _api.CreateAsync(server, scope.AccountId, scope.ProfileId,
                body with { ExpectedRevision = ended.Revision, ExpectedDownloadId = ended.Id }, cancellationToken).ConfigureAwait(false);
            return EntryOf(created, body);
        }
        catch (SiloApiException ex) when (ex.Status is 403 or 404 or 501)
        {
            _logger.LogInformation("The server does not let this profile download it ({Status})", ex.Status);
            return (null, new(DownloadStartResult.Unavailable));
        }
        catch (SiloApiException ex) when (IsLimit(ex))
        {
            _logger.LogInformation("The server's download limit for the account was reached");
            return (null, new(DownloadStartResult.Limited, RetryAfter: ex.RetryAfter));
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Another conflict (the entry changed meanwhile) or no clear answer: the next press finds what the server has.
            _logger.LogInformation("Download {DownloadId} could not be asked for again ({Failure})", ended.Id, Describe(ex));
            return (null, new(DownloadStartResult.Failed, IsFinal(ex)));
        }
    }

    // What a create came to: its result; whether a failure is one the server would give again as the create stands, which takes a
    // waiting episode out of line; the wait a refusal for the limit names (Retry-After), if any; and whether a waiting episode
    // was taken out of line while it was asked for (Dropped: nothing kept, and the server hears the delete of what it made).
    private readonly record struct CreateOutcome(DownloadStartResult Result, bool IsFinal = false, TimeSpan? RetryAfter = null, bool Dropped = false);

    // The account's limit of downloads at once or in a period (silo-server internal/downloads/limiter.go), by its problem code:
    // a 429 of another kind, as a proxy's, is no such limit.
    private static bool IsLimit(SiloApiException ex) => ex is { Status: 429, Code: ProblemCodes.RateLimited };

    // A refusal the server would give again as the create stands: a request it does not take (a 4xx other than the ones that
    // pass: 401, 408, 409, 425, 429), or an answer that cannot be read. No answer, a 5xx or another profile is not one.
    private static bool IsFinal(Exception ex) =>
        ex is JsonException || ex is SiloApiException { Status: >= 400 and < 500 and not (401 or 408 or 409 or 425 or 429) };

    // No answer, a conflict (an entry is there already) or a server error: the server may have made the entry.
    private static bool IsUncertain(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SiloApiException { Status: 409 or >= 500 }
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    // The registry's newest entry for this film or episode, when this PC does not keep it and has no delete of it to send: a live
    // one to take over, or one that ended (revoked, failed, cancelled) to revive. Neither when there is none or the registry cannot
    // be read. An entry whose delete this PC still has to send is never taken over nor revived: the delete would end it under
    // the new download. That download fails, to be tried again once the delete went (the queue sends it).
    private async Task<(DownloadEntry? Live, DownloadEntry? Ended)> FindInRegistryAsync(Uri server, DownloadScope scope, CreateDownloadBody body,
        CancellationToken cancellationToken)
    {
        var deleting = _store.PendingDeletes(scope).Select(d => d.DownloadId).ToHashSet(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (int page = 0; page < MaxPages; page++)
            {
                var read = await _api.ListAsync(server, scope.AccountId, scope.ProfileId, cursor, cancellationToken).ConfigureAwait(false);
                if (read.Items.FirstOrDefault(e => IsFor(e, body)) is { } entry)
                {
                    return !IsTakeable(scope, entry, deleting) ? default
                        : IsLive(entry) ? (entry, null)
                        : (null, entry);
                }
                if (read.Page is not { HasMore: true, NextCursor: { Length: > 0 } next } || !cursors.Add(next))
                {
                    return default;
                }
                cursor = next;
            }
            return default;
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("The download registry could not be read ({Failure})", Describe(ex));
            return default;
        }
    }

    // An entry this PC does not keep already, and has no delete of to send (then or now).
    private bool IsTakeable(DownloadScope scope, DownloadEntry entry, HashSet<string> deleting)
    {
        if (_store.Find(scope, entry.Id) is not null)
        {
            return false;
        }
        if (deleting.Contains(entry.Id) || _store.PendingDeletes(scope).Any(d => d.DownloadId == entry.Id))
        {
            _logger.LogInformation("Download {DownloadId} is still to be deleted on the server; it is not taken over, try again later", entry.Id);
            _queue.Wake();
            return false;
        }
        if (IsLive(entry))
        {
            _logger.LogInformation("The server had made download {DownloadId}; it is kept", entry.Id);
        }
        return true;
    }

    private static bool IsFor(DownloadEntry entry, CreateDownloadBody body) => entry.ContentId == body.ContentId && entry.EpisodeId == body.EpisodeId;

    private static bool IsLive(DownloadEntry entry) =>
        entry.Status is not (DownloadStatuses.Revoked or DownloadStatuses.Failed or DownloadStatuses.Cancelled);

    // The entry kept with the request's title fields, and the queue told. Once the server made it, a cancel does not stop it
    // from being kept: a create is not sent again. A sign-out since the create began (clears) keeps it off this PC; the
    // server's entry stays, as for any download whose account signed out. A waiting episode's (onlyWhileWaiting) is kept only
    // while it is still in line: one the viewer took out meanwhile is not kept, and the server hears the delete of its entry.
    private async Task<CreateOutcome> KeepAsync(DownloadScope scope, DownloadRequest request, DownloadEntry entry, long clears, bool onlyWhileWaiting)
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
            if (await _store.AddUnlessClearedAsync(record, clears, onlyWhileWaiting, CancellationToken.None).ConfigureAwait(false) is null)
            {
                if (onlyWhileWaiting && !_store.IsClearedSince(scope, clears))
                {
                    _logger.LogInformation("Download {DownloadId} came for an episode taken out of line; it is not kept", entry.Id);
                    await _store.QueueServerDeleteAsync(scope, entry.Id, CancellationToken.None).ConfigureAwait(false);
                    _queue.Wake();
                    return new(DownloadStartResult.Unavailable, Dropped: true);
                }
                return new(DownloadStartResult.Failed);
            }
        }
        catch (InvalidOperationException)
        {
            return new(DownloadStartResult.AlreadyDownloading);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server's entry stays: the next create finds it in the registry.
            _logger.LogWarning("Download {DownloadId} could not be kept on this PC ({Failure})", entry.Id, DownloadLog.Failure(ex));
            return new(DownloadStartResult.Failed);
        }
        _logger.LogInformation("Download {DownloadId} is kept, {State}", entry.Id, record.State);
        _queue.Wake();
        return new(DownloadStartResult.Started);
    }

    private static string Describe(Exception ex) => ex switch
    {
        SiloApiException api => api.Status.ToString(CultureInfo.InvariantCulture),
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => ex.GetType().Name,
    };
}
