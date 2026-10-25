using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using Granary.Core.Api;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>Tries a candidate address of the local route.</summary>
public interface ILocalRouteProbe
{
    /// <summary>
    /// A route to <paramref name="candidate"/> if it presents the key with <paramref name="pin"/> and answers as
    /// <paramref name="serverId"/>; null otherwise.
    /// </summary>
    Task<LocalRoute?> ProbeAsync(Uri savedOrigin, Uri candidate, string pin, string serverId, CancellationToken cancellationToken);
}

/// <summary>Rules 2 and 3 of a local address (server-identity.md): the pinned key, then the same identity, within 3 s.</summary>
public sealed class LocalRouteProbe(ILogger<LocalRouteProbe> logger) : ILocalRouteProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<LocalRoute?> ProbeAsync(Uri savedOrigin, Uri candidate, string pin, string serverId, CancellationToken cancellationToken)
    {
        var client = new HttpMessageInvoker(LocalRoute.PinnedHandler(pin, Timeout));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(candidate, "api/v2/system/identity"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var identity = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(SiloJsonContext.Default.ServerIdentity, timeout.Token).ConfigureAwait(false)
                : null;
            if (identity?.ServerId == serverId)
            {
                return new LocalRoute(savedOrigin, candidate, pin, client);
            }
            logger.LogInformation("{Candidate} presents the key but does not answer as the saved server", candidate.Host);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("No local route at {Candidate}: {Failure}", candidate.Host,
                ex is HttpRequestException http ? http.HttpRequestError.ToString() : ex.GetType().Name);
        }
        client.Dispose();
        return null;
    }
}

/// <summary>What the local route needs of the network: names resolved, and word when the network changes.</summary>
public interface ILocalNetwork
{
    event EventHandler? Changed;

    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class LocalNetwork : ILocalNetwork, IDisposable
{
    private EventHandler? _changed;

    public event EventHandler? Changed
    {
        add
        {
            if (_changed is null)
            {
                NetworkChange.NetworkAddressChanged += OnAddressChanged;
            }
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
            {
                NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            }
        }
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;

    private void OnAddressChanged(object? sender, EventArgs e) => _changed?.Invoke(this, EventArgs.Empty);
}
