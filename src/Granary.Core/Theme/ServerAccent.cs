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
/// anyone signs in), as the web paints it. Read when Granary starts and whenever another server becomes active. What the
/// server last answered is kept with it (<see cref="ServerEntry.Branding"/>) and painted at once, before it answers, and
/// while a read fails; without that, the last accent stays until the new one answers. The web's own light accent
/// (<see cref="AccentPalette.Default"/>) without a server, for a server without an accent, and while the read of a server
/// never read fails. A failed read is tried again after
/// 5 s, 15 s and a minute, then every 5 minutes, and at once when the session signs in or becomes ready, or the network
/// changes, until the server answers. The branding's wordmark comes with it, for the sidebar.
/// </summary>
public sealed partial class ServerAccent : ObservableObject, IDisposable
{
    /// <summary>The waits after one failed read, the next, and so on; the last repeats.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    // A network change comes in bursts (an adapter going down, another coming up): one read once it has settled.
    internal static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(2);

    private readonly ISessionManager _session;
    private readonly IServerRegistry _registry;
    private readonly BrandingApi _branding;
    private readonly ILocalNetwork _network;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerAccent> _logger;
    private bool _following;
    private bool _disposed;
    private bool _hasRead;
    private string? _readFor;
    // The server that answered at the saved address, and the branding last seen saved with it: either changing means
    // another server answers there now (it was added again), which is read as a new one.
    private string? _readServerId;
    private SavedBranding? _seenBranding;
    private SessionState _state;
    // Cancelled, never disposed: a read still running holds its token, and a source without a timer holds nothing to
    // release.
    private CancellationTokenSource? _run;
    private ITimer? _retry;
    // Whether the active server's last read failed; its streak is logged once.
    private bool _failing;
    // Which of the RetryDelays comes next.
    private int _retries;

    public ServerAccent(ISessionManager session, IServerRegistry registry, BrandingApi branding, ILocalNetwork network, IUiDispatcher dispatcher,
        TimeProvider time, ILogger<ServerAccent> logger)
    {
        _session = session;
        _registry = registry;
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
        if (_hasRead && server?.Id == _readFor && !AnotherServerAt(server))
        {
            _seenBranding = server?.Branding;
            // A sign-in, or a session that became ready, shows the server answers again.
            return _failing && server is not null && SignsIn(was, _state) ? ReadAgain(server) : Reading;
        }
        _hasRead = true;
        _readFor = server?.Id;
        _readServerId = server?.ServerId;
        _seenBranding = server?.Branding;
        _run?.Cancel();
        StopRetrying();
        _failing = false;
        _retries = 0;
        if (server is null)
        {
            Palette = AccentPalette.Default;
            Wordmark = null;
            return Task.CompletedTask;
        }
        if (server.Branding is { } saved)
        {
            Palette = AccentPalette.From(saved.Accent);
            Wordmark = Uri.TryCreate(saved.Wordmark, UriKind.Absolute, out var wordmark) ? OnTheServer(server.BaseUri, wordmark) : null;
        }
        else
        {
            Wordmark = null;
        }
        return ReadAsync(server);
    }

    private Task ReadAgain(ServerEntry server)
    {
        StopRetrying();
        return Reading = ReadAsync(server);
    }

    // Started on the UI thread, as every caller is (StartAsync, and the dispatcher for the session, the network and the
    // retries), and resumed there after each await through its synchronization context: what follows sets the properties
    // the app paints from, and the state of the reads, which only that thread touches.
    private async Task ReadAsync(ServerEntry server)
    {
        // A read started again replaces one still waiting for an answer.
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        var cancellationToken = run.Token;
        AccentPalette palette;
        Uri? wordmark;
        SavedBranding answer;
        try
        {
            var branding = await _branding.GetAsync(server.BaseUri, cancellationToken);
            palette = AccentPalette.From(branding.AccentColor);
            wordmark = OnTheServer(server.BaseUri, ArtworkUrl.Resolve(server.BaseUri, branding.WordmarkUrl));
            answer = new SavedBranding(AccentPalette.Parse(branding.AccentColor)?.ToString() ?? SavedBranding.NoAccent, wordmark?.AbsoluteUri);
        }
        catch (Exception ex)
        {
            // Any failure is tried again, one of Granary's own too: nothing ends the retries unseen.
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
        Palette = palette;
        Wordmark = wordmark;
        await RememberAsync(server, answer);
    }

    // Saved with the server, unless another server answers at its address by now; the registry writes only what differs
    // from what it has.
    private async Task RememberAsync(ServerEntry server, SavedBranding branding)
    {
        try
        {
            await _registry.UpdateAsync(server.Id, entry => entry.ServerId == server.ServerId ? entry with { Branding = branding } : entry);
        }
        catch (Exception ex)
        {
            // The accent shows all the same; it is read again at the next start. By its kind only: a file path can name the user.
            _logger.LogWarning("Could not remember the server's branding: {Failure}", ex.GetType().Name);
        }
    }

    private void Failed(ServerEntry server, Exception ex)
    {
        if (!ApiFailureText.IsRequestFailure(ex))
        {
            // Not the server's doing: logged each time, by its kind only, since its message can name the address.
            _logger.LogWarning("Reading the server's branding failed unexpectedly; trying again: {Failure}", ex.GetType().Name);
        }
        else if (!_failing)
        {
            _logger.LogWarning("Could not read the server's branding; trying again: {Failure}", ApiFailureText.Describe(ex));
        }
        _failing = true;
        if (server.Branding is null)
        {
            // Another server's accent must not stay; the web's own is the safe one. What this server answered last stays.
            Palette = AccentPalette.Default;
        }
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

    private bool AnotherServerAt(ServerEntry? server) =>
        server is not null && (server.ServerId != _readServerId || (_seenBranding is not null && server.Branding is null));

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
