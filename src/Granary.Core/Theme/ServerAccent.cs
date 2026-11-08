using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.Images;
using Granary.Core.Api.Theme;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Theme;

/// <summary>
/// The accent of the server Granary uses, from its branding (<c>GET /api/v2/theme/branding</c>, public, so known before
/// anyone signs in), as the web paints it. Read when Granary starts and whenever another server becomes active; the last
/// accent stays until the new one answers. Granary's own red without a server, for a server without an accent, and while
/// the read fails. A failed read is tried again after 5 s, 15 s and a minute, then every 5 minutes, and at once when the
/// session signs in or becomes ready, or the network changes, until the server answers. The branding's wordmark comes
/// with it, for the sidebar.
/// </summary>
public sealed partial class ServerAccent : ObservableObject, IDisposable
{
    /// <summary>The waits after one failed read, the next, and so on; the last repeats.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    // A network change comes in bursts (an adapter going down, another coming up): one read once it has settled.
    internal static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(2);

    private readonly ISessionManager _session;
    private readonly BrandingApi _branding;
    private readonly ILocalNetwork _network;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerAccent> _logger;
    private bool _following;
    private bool _disposed;
    private bool _hasRead;
    private string? _readFor;
    private SessionState _state;
    private CancellationTokenSource? _run;
    private ITimer? _retry;
    // Whether the active server's last read failed; its streak is logged once.
    private bool _failing;
    // Which of the RetryDelays comes next.
    private int _retries;

    public ServerAccent(ISessionManager session, BrandingApi branding, ILocalNetwork network, IUiDispatcher dispatcher, TimeProvider time,
        ILogger<ServerAccent> logger)
    {
        _session = session;
        _branding = branding;
        _network = network;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    [ObservableProperty]
    public partial AccentPalette Palette { get; private set; } = AccentPalette.Default;

    /// <summary>
    /// The picture the server's admin set in place of its name, as the web's sidebar shows it (components/SiloBrand.tsx);
    /// null without one, where the web falls back on Silo's own and Granary on the server's name. Another server's never
    /// stays while this one's is read.
    /// </summary>
    [ObservableProperty]
    public partial Uri? Wordmark { get; private set; }

    /// <summary>The read in flight, for tests.</summary>
    internal Task Reading { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the active server's accent, and follows the active server from then on.</summary>
    public Task StartAsync()
    {
        if (!_following && !_disposed)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
            _network.Changed += OnNetworkChanged;
        }
        return Reading = FollowAsync();
    }

    /// <summary>Stops following the session and the network, and trying again.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        _network.Changed -= OnNetworkChanged;
        _run?.Cancel();
        StopRetrying();
    }

    private Task FollowAsync()
    {
        if (_disposed)
        {
            return Reading;
        }
        var server = _session.ActiveServer;
        var was = _state;
        _state = _session.State;
        if (_hasRead && server?.Id == _readFor)
        {
            // A sign-in, or a session that became ready, shows the server answers again.
            return _failing && server is not null && SignsIn(was, _state) ? ReadAgain(server) : Reading;
        }
        _hasRead = true;
        _readFor = server?.Id;
        _run?.Cancel();
        StopRetrying();
        _failing = false;
        _retries = 0;
        Wordmark = null;
        if (server is null)
        {
            Palette = AccentPalette.Default;
            return Task.CompletedTask;
        }
        return ReadAsync(server);
    }

    private Task ReadAgain(ServerEntry server)
    {
        StopRetrying();
        return Reading = ReadAsync(server);
    }

    private async Task ReadAsync(ServerEntry server)
    {
        // A read started again replaces one still waiting for an answer.
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        var cancellationToken = run.Token;
        ServerBranding branding;
        try
        {
            branding = await _branding.GetAsync(server.BaseUri, cancellationToken);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            if (!cancellationToken.IsCancellationRequested && _readFor == server.Id)
            {
                Failed(server, ex);
            }
            return;
        }
        if (cancellationToken.IsCancellationRequested || _readFor != server.Id)
        {
            return;
        }
        _failing = false;
        _retries = 0;
        Palette = AccentPalette.From(branding.AccentColor);
        Wordmark = OnTheServer(server.BaseUri, ArtworkUrl.Resolve(server.BaseUri, branding.WordmarkUrl));
    }

    private void Failed(ServerEntry server, Exception ex)
    {
        if (!_failing)
        {
            _failing = true;
            _logger.LogWarning("Could not read the server's branding; trying again: {Failure}", ApiFailureText.Describe(ex));
        }
        // Another server's accent must not stay; Granary's own is the safe one.
        Palette = AccentPalette.Default;
        RetryIn(RetryDelays[Math.Min(_retries, RetryDelays.Length - 1)], server.Id);
        _retries++;
    }

    private void RetryIn(TimeSpan delay, string serverId)
    {
        StopRetrying();
        _retry = _time.CreateTimer(_ => _dispatcher.Post(() => RetryDue(serverId)), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void RetryDue(string serverId)
    {
        if (!_disposed && _failing && _readFor == serverId && _session.ActiveServer is { } server && server.Id == serverId)
        {
            ReadAgain(server);
        }
    }

    private void StopRetrying()
    {
        _retry?.Dispose();
        _retry = null;
    }

    // Signed in where it was not, or ready where it was not.
    private static bool SignsIn(SessionState was, SessionState now) =>
        (now == SessionState.Ready && was != SessionState.Ready) || (IsSignedIn(now) && !IsSignedIn(was));

    private static bool IsSignedIn(SessionState state) => state is not (SessionState.NoServer or SessionState.SignedOut);

    // The server names its own asset, always relative to itself (silo-server internal/branding/render.go). A picture from
    // anywhere else is not its wordmark, and fetching it would tell that host about this PC.
    private static Uri? OnTheServer(Uri server, Uri? url) =>
        url is not null && Uri.Compare(url, server, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? url
            : null;

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Reading = FollowAsync());

    // From a pool thread. Only a failing read is tried again, and the waits start over.
    private void OnNetworkChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed && _failing && _readFor is { } serverId)
        {
            _retries = 0;
            RetryIn(NetworkSettle, serverId);
        }
    });
}
