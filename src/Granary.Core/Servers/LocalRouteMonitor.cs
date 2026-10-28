using System.Net;
using System.Net.Sockets;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>
/// Finds and keeps the local route of the active server (docs/api-contract.md, 9.7; server-identity.md), as the
/// Apple client does: reads the key and the local address through the saved address, reduces that address and the
/// server's <c>_silo._tcp</c> adverts (<see cref="LanAdvertWatch"/>) to private IPv4 addresses, and uses the first that
/// presents the key over HTTPS on the same port and answers as the same server. Checks again when the session or the
/// network changes, when an advert of the server comes or moves while there is no route (at most every 30 s: anyone can
/// advertise the server's id), and every 15 minutes. Only a
/// saved HTTPS address without a path prefix gets a local route: a plain HTTP one cannot vouch for a key, and the local
/// address serves the API at its root.
/// </summary>
public sealed class LocalRouteMonitor : IDisposable
{
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(15);

    // A network change comes in bursts (an adapter going down, another coming up).
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(2);

    // Adverts are anyone's to send: those of the server set off a check at most this often.
    internal static readonly TimeSpan AdvertRecheckInterval = TimeSpan.FromSeconds(30);

    // The server names the local address: a check stays small whatever it resolves to.
    private const int MaxCandidates = 4;
    private static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(15);

    private readonly SystemApi _system;
    private readonly ISessionManager _session;
    private readonly LocalRoutes _routes;
    private readonly ILocalRouteProbe _probe;
    private readonly ILocalNetwork _network;
    private readonly LanAdvertWatch _adverts;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalRouteMonitor> _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly Lock _gate = new();
    private ITimer? _recheck;
    private ITimer? _settle;
    private ITimer? _advertCheck;
    private DateTimeOffset _lastAdvertCheck = DateTimeOffset.MinValue;

    // The local origin that last presented the key, for this server: tried first among the adverts.
    private Uri? _lastConfirmed;
    private Uri? _server;
    private int _started;
    private int _queued;
    private volatile bool _disposed;

    public LocalRouteMonitor(SystemApi system, ISessionManager session, LocalRoutes routes, ILocalRouteProbe probe, ILocalNetwork network,
        LanAdvertWatch adverts, TimeProvider time, ILogger<LocalRouteMonitor> logger)
    {
        _system = system;
        _session = session;
        _routes = routes;
        _probe = probe;
        _network = network;
        _adverts = adverts;
        _time = time;
        _logger = logger;
    }

    /// <summary>Checks now, and from then on whenever the session or the network changes, and every 15 minutes.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        _session.Changed += OnSessionChanged;
        _network.Changed += OnNetworkChanged;
        _adverts.Changed += OnAdvertsChanged;
        _recheck = _time.CreateTimer(_ => Check(), null, TimeSpan.Zero, Recheck);
    }

    /// <summary>One check of the active server's local route.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _queued, 0);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CheckBudget);
        try
        {
            await CheckOnceAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Checking the local route took too long; trying again later");
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // The saved address is out of reach (no internet at home, say): a route verified before keeps working
            // until it fails to connect itself.
            _logger.LogInformation("Could not read the server's connections: {Failure}", ApiFailureText.Describe(ex));
        }
        finally
        {
            _checking.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        _network.Changed -= OnNetworkChanged;
        _adverts.Changed -= OnAdvertsChanged;
        _adverts.Unwatch();
        _recheck?.Dispose();
        lock (_gate)
        {
            _settle?.Dispose();
            _advertCheck?.Dispose();
        }
    }

    /// <summary>The private IPv4 addresses behind <paramref name="localUrl"/>, as HTTPS origins on its port.</summary>
    internal async Task<IReadOnlyList<Uri>> CandidatesAsync(string? localUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.HostNameType == UriHostNameType.Unknown)
        {
            return [];
        }
        IPAddress[] addresses;
        if (IPAddress.TryParse(url.IdnHost, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _network.ResolveAsync(url.IdnHost, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogInformation("The server's local address does not resolve here: {Error}", ex.SocketErrorCode);
                return [];
            }
        }
        // HTTPS on the local address's own port, whatever scheme it names (server-identity.md, rule 1).
        return [.. addresses.Where(IsPrivateIPv4).Distinct().Take(MaxCandidates).Select(address => new Uri($"https://{address}:{url.Port}/"))];
    }

    // RFC 1918: no public certificate authority certifies these, so the pin is the only trust that applies.
    internal static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool CanHaveLocalRoute(Uri saved) =>
        saved.Scheme == Uri.UriSchemeHttps && saved.AbsolutePath == "/" && saved.Query.Length == 0;

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var server = _session.State is SessionState.ProfileRequired or SessionState.Ready ? _session.ActiveServer : null;
        if (_server is { } previous && previous != server?.BaseUri)
        {
            _routes.Drop(previous);
            _lastConfirmed = null;
        }
        _server = server?.BaseUri;
        if (server is null || !CanHaveLocalRoute(server.BaseUri))
        {
            _adverts.Unwatch();
            return;
        }
        var connections = await _system.GetConnectionsAsync(server.BaseUri, cancellationToken).ConfigureAwait(false);
        if (server.ServerId is { } known && known != connections.ServerId)
        {
            // The saved address answers for another deployment now: nothing it says is about this server.
            _logger.LogWarning("The saved address answers as another server; no local route");
            _adverts.Unwatch();
            _routes.Drop(server.BaseUri);
            return;
        }
        if (connections.LocalRoute is not { SpkiSha256.Length: > 0 } local)
        {
            _adverts.Unwatch();
            _routes.Drop(server.BaseUri);
            return;
        }
        var current = _routes.Find(server.BaseUri);
        // An id an advert cannot carry leaves only the administrator's address. Of the adverts, the one in use or last
        // confirmed goes first: others with the same id cannot crowd it out.
        IReadOnlyList<Uri> advertised = [];
        if (LanAdverts.UsableId(connections.ServerId) is { } serverId)
        {
            _adverts.Watch(serverId);
            advertised = _adverts.Origins(serverId, current?.Origin ?? _lastConfirmed);
        }
        else
        {
            _adverts.Unwatch();
        }
        // The administrator's address first, then the adverts: each only a candidate the pin decides on.
        var candidates = (await CandidatesAsync(local.Url, cancellationToken).ConfigureAwait(false)).Concat(advertised).Distinct();
        foreach (var candidate in candidates)
        {
            if (current is not null && current.Origin == candidate && current.Pin == local.SpkiSha256)
            {
                return;
            }
            if (await _probe.ProbeAsync(server.BaseUri, candidate, local.SpkiSha256, connections.ServerId, cancellationToken).ConfigureAwait(false) is { } route)
            {
                if (_disposed)
                {
                    route.Dispose();
                    return;
                }
                _logger.LogInformation("Using the local route to the server at {Address}", candidate.Authority);
                _routes.Use(route);
                _lastConfirmed = candidate;
                return;
            }
        }
        if (current is not null)
        {
            _logger.LogInformation("The local route is gone; using the saved address");
        }
        _routes.Drop(server.BaseUri);
    }

    // One check waits at most behind the one running: events in a burst share it.
    private void Check()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checking the local route failed unexpectedly");
            }
        });
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Check();

    private void OnNetworkChanged(object? sender, EventArgs e) => CheckWhenSettled();

    // An advert that came or moved is worth a check only while there is no route. Adverts come in bursts and anyone can
    // send them: one check waits for the burst to settle, and the next not before 30 s after the last; more adverts in
    // the meantime neither add a check nor put the waiting one off.
    private void OnAdvertsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _server is not { } server || _routes.Find(server) is not null)
        {
            return;
        }
        lock (_gate)
        {
            if (_advertCheck is not null)
            {
                return;
            }
            var wait = _lastAdvertCheck == DateTimeOffset.MinValue ? NetworkSettle : _lastAdvertCheck + AdvertRecheckInterval - _time.GetUtcNow();
            _advertCheck = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    _advertCheck?.Dispose();
                    _advertCheck = null;
                    _lastAdvertCheck = _time.GetUtcNow();
                }
                Check();
            }, null, wait < NetworkSettle ? NetworkSettle : wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckWhenSettled()
    {
        lock (_gate)
        {
            _settle?.Dispose();
            _settle = _time.CreateTimer(_ => Check(), null, NetworkSettle, Timeout.InfiniteTimeSpan);
        }
    }
}
