using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.Images;
using Granary.Core.Api.Theme;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Theme;

/// <summary>
/// The accent of the server Granary uses, from its branding (<c>GET /api/v2/theme/branding</c>, public, so known before
/// anyone signs in), as the web paints it. Read when Granary starts and whenever another server becomes active; the last
/// accent stays until the new one answers. Granary's own red without a server, for a server without an accent, and when
/// the read fails. The branding's wordmark comes with it, for the sidebar.
/// </summary>
public sealed partial class ServerAccent : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly BrandingApi _branding;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ServerAccent> _logger;
    private bool _following;
    private bool _hasRead;
    private string? _readFor;
    private CancellationTokenSource? _run;

    public ServerAccent(ISessionManager session, BrandingApi branding, IUiDispatcher dispatcher, ILogger<ServerAccent> logger)
    {
        _session = session;
        _branding = branding;
        _dispatcher = dispatcher;
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
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Reading = ReadIfNewAsync();
    }

    private Task ReadIfNewAsync()
    {
        var server = _session.ActiveServer;
        if (_hasRead && server?.Id == _readFor)
        {
            return Task.CompletedTask;
        }
        _hasRead = true;
        _readFor = server?.Id;
        _run?.Cancel();
        Wordmark = null;
        if (server is null)
        {
            Palette = AccentPalette.Default;
            return Task.CompletedTask;
        }
        var run = new CancellationTokenSource();
        _run = run;
        return ReadAsync(server.Id, server.BaseUri, run.Token);
    }

    private async Task ReadAsync(string serverId, Uri server, CancellationToken cancellationToken)
    {
        AccentPalette palette;
        Uri? wordmark = null;
        try
        {
            var branding = await _branding.GetAsync(server, cancellationToken);
            palette = AccentPalette.From(branding.AccentColor);
            wordmark = OnTheServer(server, ArtworkUrl.Resolve(server, branding.WordmarkUrl));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ApiFailureText.IsRequestFailure(ex))
        {
            // Another server's accent must not stay; Granary's own is the safe one.
            _logger.LogWarning("Could not read the server's branding: {Failure}", ApiFailureText.Describe(ex));
            palette = AccentPalette.Default;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested && _readFor == serverId)
        {
            Palette = palette;
            Wordmark = wordmark;
        }
    }

    // The server names its own asset, always relative to itself (silo-server internal/branding/render.go). A picture from
    // anywhere else is not its wordmark, and fetching it would tell that host about this PC.
    private static Uri? OnTheServer(Uri server, Uri? url) =>
        url is not null && Uri.Compare(url, server, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? url
            : null;

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Reading = ReadIfNewAsync());
}
