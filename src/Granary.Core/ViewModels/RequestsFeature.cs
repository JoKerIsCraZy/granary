using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Whether the server takes requests from the profile (<c>GET /api/v2/requests/status</c>): the sidebar's Requests entry
/// shows only then, as on the web. Read once per profile. A server without the call (404) takes none; another failure
/// hides the entry and is tried again after <see cref="RetryAfter"/>.
/// </summary>
public sealed partial class RequestsFeature : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly TimeProvider _time;
    private readonly ILogger<RequestsFeature> _logger;
    private (string? ServerId, string? ProfileId)? _readFor;
    private (string? ServerId, string? ProfileId)? _asking;
    // The read in flight, which a second caller for the same profile waits for.
    private Task? _reading;
    private ((string? ServerId, string? ProfileId) Key, DateTimeOffset At)? _failed;

    public RequestsFeature(ISessionManager session, RequestsApi requests, TimeProvider time, ILogger<RequestsFeature> logger)
    {
        _session = session;
        _requests = requests;
        _time = time;
        _logger = logger;
    }

    /// <summary>How long a failed read keeps the entry hidden before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    [ObservableProperty]
    public partial bool IsEnabled { get; private set; }

    /// <summary>What the server said, for the request pages: download progress, follows, seasons.</summary>
    [ObservableProperty]
    public partial RequestFeatureStatus? Status { get; private set; }

    /// <summary>Reads the status for a profile it has not read it for.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _readFor = null;
            IsEnabled = false;
            Status = null;
            return;
        }
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_asking == key && _reading is { } reading)
        {
            // Another caller is reading it: its answer is this one's too.
            await reading.WaitAsync(cancellationToken);
            return;
        }
        if (_readFor == key || (_failed is { } failed && failed.Key == key && _time.GetUtcNow() - failed.At < RetryAfter))
        {
            return;
        }
        if (_readFor is not null)
        {
            // Another profile: nothing of the last one's applies.
            _readFor = null;
            IsEnabled = false;
            Status = null;
        }
        _asking = key;
        // The read is everyone's: a caller that gives up stops waiting, it does not end the read for the others.
        var read = ReadAsync(server, key, CancellationToken.None);
        _reading = read;
        await read.WaitAsync(cancellationToken);
    }

    private async Task ReadAsync(ServerEntry server, (string? ServerId, string? ProfileId) key, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _requests.GetStatusAsync(server.BaseUri, cancellationToken);
            if ((_session.ActiveServer?.Id, _session.ProfileId) == key)
            {
                _readFor = key;
                _failed = null;
                Status = status;
                IsEnabled = status.RequestsEnabled;
            }
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // A server without requests: nothing to ask again.
            _logger.LogInformation("The server has no requests");
            if ((_session.ActiveServer?.Id, _session.ProfileId) == key)
            {
                _readFor = key;
                IsEnabled = false;
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read whether the server takes requests");
            _failed = (key, _time.GetUtcNow());
            // A late failure for a profile left since leaves the current one's answer alone.
            if ((_session.ActiveServer?.Id, _session.ProfileId) == key)
            {
                IsEnabled = false;
            }
        }
        finally
        {
            if (_asking == key)
            {
                _asking = null;
            }
        }
    }
}
