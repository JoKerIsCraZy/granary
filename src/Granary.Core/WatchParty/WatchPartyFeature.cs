using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>
/// Whether the server offers Watch Party rooms to this account (<c>GET /api/v2/watch-together/capabilities</c>): the
/// sidebar's Watch Party entry shows only then, as on the web. Read once per profile; a failed read hides the entry
/// and is tried again after <see cref="RetryAfter"/>.
/// </summary>
public sealed partial class WatchPartyFeature : ObservableObject
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly ISessionManager _session;
    private readonly WatchPartyApi _api;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyFeature> _logger;
    private (string? ServerId, string? ProfileId)? _readFor;
    private ((string? ServerId, string? ProfileId) Key, DateTimeOffset At)? _failed;

    public WatchPartyFeature(ISessionManager session, WatchPartyApi api, TimeProvider time, ILogger<WatchPartyFeature> logger)
    {
        _session = session;
        _api = api;
        _time = time;
        _logger = logger;
    }

    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>What the server offers; null until read.</summary>
    [ObservableProperty]
    public partial WatchPartyCapabilities? Capabilities { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _readFor = null;
            IsAvailable = false;
            Capabilities = null;
            return;
        }
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_readFor == key || (_failed is { } failed && failed.Key == key && _time.GetUtcNow() - failed.At < RetryAfter))
        {
            return;
        }
        _readFor = key;
        try
        {
            var capabilities = await _api.GetCapabilitiesAsync(server.BaseUri, cancellationToken);
            Capabilities = capabilities;
            IsAvailable = capabilities.IsAvailable;
            _failed = null;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read whether the server offers Watch Party");
            _readFor = null;
            _failed = (key, _time.GetUtcNow());
            IsAvailable = false;
            Capabilities = null;
        }
    }
}
