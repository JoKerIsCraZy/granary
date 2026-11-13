using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Whether the server lets this profile download (<c>GET /api/v2/capabilities/downloads</c>), as Silo's Apple app gates it:
/// Download actions show only when it does. The sidebar's Downloads entry shows then, or while the profile has downloads on
/// this PC, which stay playable and deletable whatever the server says, and show the entry before the server answered. Read
/// once per profile; a failed read is tried again after <see cref="RetryAfter"/>.
/// </summary>
public sealed partial class DownloadsFeature : ObservableObject
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly ISessionManager _session;
    private readonly DownloadsApi _api;
    private readonly DownloadStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadsFeature> _logger;
    private DownloadScope? _readFor;
    // Which read is the latest: an earlier one answering or failing late changes nothing.
    private int _reads;
    private (DownloadScope Scope, DateTimeOffset At)? _failed;

    public DownloadsFeature(ISessionManager session, DownloadsApi api, DownloadStore store, TimeProvider time, ILogger<DownloadsFeature> logger)
    {
        _session = session;
        _api = api;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>What the server offers the active profile; null until read, and when it could not be.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial DownloadCapability? Capability { get; private set; }

    /// <summary>The server has downloads on and lets this profile download.</summary>
    public bool CanDownload => Capability?.IsUsable == true;

    /// <summary>Downloads are offered, or the profile has some on this PC: the sidebar's entry shows.</summary>
    [ObservableProperty]
    public partial bool IsOffered { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (DownloadScope.Of(_session) is not { } scope || _session.ActiveServer is not { } server)
        {
            _readFor = null;
            Capability = null;
            IsOffered = false;
            return;
        }
        await _store.LoadAsync(cancellationToken);
        // Downloads on this PC show the entry whatever the server says, so it does not wait for an answer: a server that
        // cannot be reached can take the connection's whole timeout to fail, while the Downloads page is already open.
        // Not for a profile that is no longer the active one: its downloads are not the next one's.
        if (DownloadScope.Of(_session) == scope && _store.In(scope).Count > 0)
        {
            IsOffered = true;
        }
        if (_readFor != scope && !(_failed is { } failed && failed.Scope == scope && _time.GetUtcNow() - failed.At < RetryAfter))
        {
            await ReadAsync(scope, server.BaseUri, cancellationToken);
        }
        // A refresh for a profile that is no longer the active one leaves the entry to that one's refresh.
        if (DownloadScope.Of(_session) == scope)
        {
            IsOffered = CanDownload || _store.In(scope).Count > 0;
        }
    }

    // A refresh while this one is under way does not read again. One that does not end with an answer (a failure, a
    // cancellation, the profile changing meanwhile) leaves the next refresh to read again; a slow answer for a profile that is
    // no longer the active one is dropped, and so is any read a newer one started after: only the latest one counts, also
    // when the profile changed and came back meanwhile.
    private async Task ReadAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        // Another profile's answer is never shown for this one.
        if (_readFor != scope)
        {
            Capability = null;
        }
        _readFor = scope;
        int read = ++_reads;
        bool answered = false;
        try
        {
            var capability = await _api.GetCapabilityAsync(server, scope.ProfileId, cancellationToken);
            if (read == _reads && DownloadScope.Of(_session) == scope)
            {
                Capability = capability;
                _failed = null;
                answered = true;
            }
        }
        catch (Exception ex) when ((LibraryErrors.IsExpected(ex) || ex is ProfileChangedException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not read whether the server offers downloads ({Failure})",
                ex is SiloApiException api ? api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name);
            if (read == _reads)
            {
                _failed = (scope, _time.GetUtcNow());
                Capability = null;
            }
        }
        finally
        {
            if (!answered && read == _reads)
            {
                _readFor = null;
            }
        }
    }
}
