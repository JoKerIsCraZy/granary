namespace Granary.Core.Api.Discovery;

/// <summary>Public pre-login documents of a server (docs/api-contract.md, 3.1).</summary>
public sealed class SystemApi(SiloHttp http)
{
    public Task<ServerIdentity> GetIdentityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/system/identity", SiloAuth.Anonymous, SiloJsonContext.Default.ServerIdentity, cancellationToken);

    public Task<SetupStatus> GetSetupStatusAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/system/setup", SiloAuth.Anonymous, SiloJsonContext.Default.SetupStatus, cancellationToken);

    public Task<Branding> GetBrandingAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/theme/branding", SiloAuth.Anonymous, SiloJsonContext.Default.Branding, cancellationToken);

    /// <summary>
    /// The local route's key and address. Read only through the saved address, which a public certificate
    /// authenticates: only that can vouch for the key (docs/api-contract.md, 9.7).
    /// </summary>
    public Task<ServerConnections> GetConnectionsAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetFromSavedAddressAsync(server, "api/v2/system/connections", SiloAuth.Account, SiloJsonContext.Default.ServerConnections, cancellationToken);
}
