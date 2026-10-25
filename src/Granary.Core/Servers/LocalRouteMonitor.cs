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
/// Apple and Android clients do: reads the key and the local address through the saved address, reduces that address
/// to private IPv4 addresses, and uses the first that presents the key over HTTPS on the same port and answers as the
/// same server. Checks again when the session or the network changes, and every 15 minutes. Only a saved HTTPS
/// address without a path prefix gets a local route: a plain HTTP one cannot vouch for a key, and the local
/// address serves the API at its root.
/// </summary>
public sealed class LocalRouteMonitor : IDisposable
{
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(15);

    // A network change comes in bursts (an adapter going down, another coming up).
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(2);

    private readonly SystemApi _system;
    private readonly ISessionManager _session;
    private readonly LocalRoutes _routes;
    private readonly ILocalRouteProbe _probe;
    private readonly ILocalNetwork _network;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalRouteMonitor> _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private ITimer? _recheck;
    private ITimer? _settle;
    private Uri? _server;
    private int _started;

    public LocalRouteMonitor(SystemApi system, ISessionManager session, LocalRoutes routes, ILocalRouteProbe probe, ILocalNetwork network,
        TimeProvider time, ILogger<LocalRouteMonitor> logger)
    {
        _system = system;
        _session = session;
        _routes = routes;
        _probe = probe;
        _network = network;
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
        _recheck = _time.CreateTimer(_ => Check(), null, TimeSpan.Zero, Recheck);
    }

    /// <summary>One check of the active server's local route.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
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
        _session.Changed -= OnSessionChanged;
        _network.Changed -= OnNetworkChanged;
        _recheck?.Dispose();
        _settle?.Dispose();
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
        return [.. addresses.Where(IsPrivateIPv4).Distinct().Select(address => new Uri($"https://{address}:{url.Port}/"))];
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
        }
        _server = server?.BaseUri;
        if (server is null || !CanHaveLocalRoute(server.BaseUri))
        {
            return;
        }
        var connections = await _system.GetConnectionsAsync(server.BaseUri, cancellationToken).ConfigureAwait(false);
        if (server.ServerId is { } known && known != connections.ServerId)
        {
            // The saved address answers for another deployment now: nothing it says is about this server.
            _logger.LogWarning("The saved address answers as another server; no local route");
            _routes.Drop(server.BaseUri);
            return;
        }
        if (connections.LocalRoute is not { SpkiSha256.Length: > 0 } local)
        {
            _routes.Drop(server.BaseUri);
            return;
        }
        var current = _routes.Find(server.BaseUri);
        foreach (var candidate in await CandidatesAsync(local.Url, cancellationToken).ConfigureAwait(false))
        {
            if (current is not null && current.Origin == candidate && current.Pin == local.SpkiSha256)
            {
                return;
            }
            if (await _probe.ProbeAsync(server.BaseUri, candidate, local.SpkiSha256, connections.ServerId, cancellationToken).ConfigureAwait(false) is { } route)
            {
                _logger.LogInformation("Using the local route to the server at {Address}", candidate.Authority);
                _routes.Use(route);
                return;
            }
        }
        if (current is not null)
        {
            _logger.LogInformation("The local route is gone; using the saved address");
        }
        _routes.Drop(server.BaseUri);
    }

    private void Check() => _ = Task.Run(async () =>
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

    private void OnSessionChanged(object? sender, EventArgs e) => Check();

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _settle?.Dispose();
        _settle = _time.CreateTimer(_ => Check(), null, NetworkSettle, Timeout.InfiniteTimeSpan);
    }
}
