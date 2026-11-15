namespace Granary.Core.Remote;

/// <summary>
/// Advertises the receiver on the local network with DNS-SD, so Silo's phones list it. The App implements it with
/// Windows' <c>DnsServiceRegister</c>; Core never opens a socket.
/// </summary>
public interface IServiceAdvertiser
{
    /// <summary>
    /// Registers <paramref name="registration"/>, replacing one made before. False when Windows refused it: the receiver
    /// then runs on, and phones connect through the pairing code only.
    /// </summary>
    Task<bool> RegisterAsync(ServiceRegistration registration, CancellationToken cancellationToken);

    /// <summary>Removes the registration. Safe to call twice, and when nothing is registered.</summary>
    Task UnregisterAsync();
}

/// <summary>Core's stand-in where the App gives no advertiser: nothing is registered, and phones use the pairing code.</summary>
public sealed class NoServiceAdvertiser : IServiceAdvertiser
{
    public Task<bool> RegisterAsync(ServiceRegistration registration, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task UnregisterAsync() => Task.CompletedTask;
}
