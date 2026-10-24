namespace Granary.Core.Api.Auth;

/// <summary>Device (QR code) sign-in from this PC's side (docs/api-contract.md, 4.2). All calls are public.</summary>
public sealed class DeviceLoginApi(SiloHttp http)
{
    public Task<DeviceLoginCapability> GetCapabilityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/auth/device/capability", SiloAuth.Anonymous, SiloJsonContext.Default.DeviceLoginCapability, cancellationToken);

    /// <summary>Opens a pairing request. Non-retryable.</summary>
    public Task<DeviceLoginStart> StartAsync(Uri server, string? deviceName, string? devicePlatform, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/auth/device/start", SiloAuth.Anonymous, new StartDeviceLoginRequest(deviceName, devicePlatform),
            SiloJsonContext.Default.StartDeviceLoginRequest, SiloJsonContext.Default.DeviceLoginStart, cancellationToken);

    /// <summary>Polls the request. The approving poll returns the tokens exactly once. Non-retryable.</summary>
    public Task<DeviceLoginPoll> PollAsync(Uri server, string deviceCode, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/auth/device/poll", SiloAuth.Anonymous, new DeviceCodeRequest(deviceCode),
            SiloJsonContext.Default.DeviceCodeRequest, SiloJsonContext.Default.DeviceLoginPoll, cancellationToken);

    /// <summary>Withdraws the request (idempotent).</summary>
    public Task<DeviceLoginCancel> CancelAsync(Uri server, string deviceCode, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/auth/device/cancel", SiloAuth.Anonymous, new DeviceCodeRequest(deviceCode),
            SiloJsonContext.Default.DeviceCodeRequest, SiloJsonContext.Default.DeviceLoginCancel, cancellationToken);
}
