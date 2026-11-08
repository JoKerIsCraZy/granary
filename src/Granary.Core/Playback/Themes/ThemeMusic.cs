using System.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback.Themes;

/// <summary>
/// Decides when title pages play their theme music, for one app-wide player, as the apps do (Android's
/// <c>ThemeMusicCoordinator</c> and <c>DetailThemeMusic</c>, Apple's <c>ThemeMusicCenter</c>, both after the web's
/// <c>useThemeMusic.ts</c>):
/// <list type="bullet">
/// <item>only while Title Pages → Theme music is on and the server serves themes to the profile (asked again once the answer
/// is a minute old);</item>
/// <item>only for the page shown last, once it has loaded: a page that leaves, or is still loading, fades its theme out and
/// pauses it, and the theme stops two seconds later unless a page shows the same one again, a season with its series' set,
/// which goes on where it was;</item>
/// <item>never while the player, its bar included, or a trailer plays: the theme stops at once and starts over after;</item>
/// <item>a minimized window counts as a page that left;</item>
/// <item>a profile, server or sign-out stops it at once, and the next profile is asked again.</item>
/// </list>
/// Used on the UI thread. Pages hand in a token of their own, which is let go as they leave.
/// </summary>
public sealed class ThemeMusic : IDisposable
{
    /// <summary>Long enough for a page to follow another, or a quick trip away from the window.</summary>
    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long the server's answer stands, as the web's query and the apps keep it.</summary>
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromSeconds(60);

    private readonly ISessionManager _session;
    private readonly TitlePageSettings _settings;
    private readonly ThemesApi _api;
    private readonly PlaybackApi _playback;
    private readonly ThemeAudioDownloader _downloader;
    private readonly IThemeAudioOutput _output;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<ThemeMusic> _logger;
    private readonly ThemeMusicPlayer _player;

    private object? _page;
    private ThemeSongSet? _themes;
    private bool _otherMedia;
    private bool _minimized;
    private ITimer? _release;
    private (string? ServerId, string? ProfileId) _identity;
    // Bumped by a reset: an answer for the identity before is dropped.
    private int _identityVersion;
    private bool? _available;
    private long _availableAt;
    // Where the answer lets a grant's audio come from: the server's own origin, or also a proxy node's.
    private ThemeOrigins _origins;
    private bool _readingCapability;
    private bool _disposed;

    public ThemeMusic(ISessionManager session, TitlePageSettings settings, ThemesApi api, PlaybackApi playback,
        ThemeAudioDownloader downloader, IThemeAudioOutput output, IUiDispatcher dispatcher, TimeProvider time,
        ILogger<ThemeMusic> logger)
    {
        _session = session;
        _settings = settings;
        _api = api;
        _playback = playback;
        _downloader = downloader;
        _output = output;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _player = new ThemeMusicPlayer(FetchAsync, output, dispatcher, time, logger);
        _identity = Identity();
        _settings.PropertyChanged += OnSettingsChanged;
        _session.Changed += OnSessionChanged;
    }

    /// <summary>Theme audio may be coming out now, a fade-out included.</summary>
    public bool IsAudible => _player.IsAudible;

    /// <summary>The player, its bar included, or a trailer holds a title: no theme plays meanwhile.</summary>
    public bool OtherMediaPlaying
    {
        get => _otherMedia;
        set
        {
            if (_otherMedia != value)
            {
                _otherMedia = value;
                Apply();
            }
        }
    }

    /// <summary>The window is minimized: its page's theme goes as if the page had left, and comes back with it.</summary>
    public bool IsMinimized
    {
        get => _minimized;
        set
        {
            if (_minimized != value)
            {
                _minimized = value;
                Apply();
            }
        }
    }

    /// <summary>Page <paramref name="page"/> is on screen with its themes; null while it is still loading.</summary>
    public void Show(object page, ThemeSongSet? themes)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_disposed)
        {
            return;
        }
        _page = page;
        _themes = themes;
        Apply();
    }

    /// <summary>Page <paramref name="page"/> left; nothing changes when another page has shown since.</summary>
    public void Hide(object page)
    {
        if (_disposed || !ReferenceEquals(_page, page))
        {
            return;
        }
        _page = null;
        _themes = null;
        Leave();
    }

    public void Dispose()
    {
        // For good: nothing that comes after, a page, a setting or a session change, plays again.
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        _session.Changed -= OnSessionChanged;
        CancelRelease();
        _player.Stop(immediate: true);
    }

    private void Apply()
    {
        if (_disposed)
        {
            return;
        }
        if (Identity() != _identity)
        {
            Reset();
        }
        if (_page is null)
        {
            // A page that left is already fading out and lets go once its grace has passed.
            return;
        }
        // Only the acting profile's own choice turns it on, never the profile's before a switch.
        if (!_settings.KnowsActiveProfile || !_settings.Shown.PlaysThemeMusic || _session.State != SessionState.Ready)
        {
            Stop(immediate: false);
            return;
        }
        if (_otherMedia)
        {
            // Playback is starting: the theme ends at once, so coming back starts it over instead of resuming it.
            Stop(immediate: true);
            return;
        }
        if (_themes is null || _minimized)
        {
            Leave();
            return;
        }
        if (Available() != true || _themes.Items.Count == 0)
        {
            Stop(immediate: false);
            return;
        }
        CancelRelease();
        _player.Select(_themes, _settings.Shown.LoopsThemeMusic);
    }

    // Fades out and pauses; the theme stops after the grace unless a page shows it again.
    private void Leave()
    {
        _player.Suspend();
        if (_release is null)
        {
            ITimer? release = null;
            release = _release = _time.CreateTimer(_ => _dispatcher.Post(() => Release(release)), null, Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            release.Change(ReleaseGrace, Timeout.InfiniteTimeSpan);
        }
    }

    // Only the grace that runs now lets go: one cancelled after its tick was posted leaves the next one alone.
    private void Release(ITimer? release)
    {
        if (release is null || !ReferenceEquals(_release, release))
        {
            return;
        }
        CancelRelease();
        if (_page is null || _themes is null || _minimized)
        {
            _player.Stop(immediate: true);
        }
    }

    private void Stop(bool immediate)
    {
        CancelRelease();
        _player.Stop(immediate);
    }

    private void CancelRelease()
    {
        _release?.Dispose();
        _release = null;
    }

    // The server's answer while it is fresh; otherwise it is asked again, and the answer before stands meanwhile.
    private bool? Available()
    {
        bool fresh = _available is not null && _time.GetElapsedTime(_availableAt) < CapabilityLifetime;
        if (!fresh)
        {
            _ = ReadCapabilityAsync();
        }
        return _available;
    }

    private async Task ReadCapabilityAsync()
    {
        if (_readingCapability || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        _readingCapability = true;
        int version = _identityVersion;
        bool? available = null;
        var origins = ThemeOrigins.Server;
        try
        {
            var capability = await _api.GetCapabilityAsync(server.BaseUri);
            available = capability.IsAvailable;
            if (available == true && capability.IsRouted)
            {
                origins = await RoutedOriginsAsync(server.BaseUri);
            }
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // A server without the route predates themes.
            available = false;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("Reading whether the server plays theme music failed: {Failure}", ApiFailureText.Describe(ex));
        }
        catch (Exception ex)
        {
            // Unforeseen: by its type alone. Themes are optional; the next page asks again.
            _logger.LogWarning("Reading whether the server plays theme music failed unexpectedly: {Failure}", ex.GetType().Name);
        }
        finally
        {
            // Only this identity's read: a reset meanwhile cleared the flag, and a newer read may hold it now.
            if (version == _identityVersion)
            {
                _readingCapability = false;
            }
        }
        if (version != _identityVersion)
        {
            return;
        }
        if (available is { } answer)
        {
            // A failed read keeps the answer before and is tried with the next page, never in a loop.
            _available = answer;
            _origins = origins;
            _availableAt = _time.GetTimestamp();
            Apply();
        }
    }

    // Routed themes may come from a proxy node. A server whose playback offers authorized_media_origins_v1 vouches for its
    // nodes, as for video; one that does not, or a read that fails, has them checked conservatively.
    private async Task<ThemeOrigins> RoutedOriginsAsync(Uri server)
    {
        try
        {
            var playback = await _playback.GetCapabilitiesAsync(server);
            return playback.HasFeature(PlaybackFeatures.AuthorizedMediaOriginsV1) ? ThemeOrigins.Authorized : ThemeOrigins.Routed;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("Reading whether the server vouches for its media origins failed: {Failure}", ApiFailureText.Describe(ex));
            return ThemeOrigins.Routed;
        }
    }

    /// <summary>The fetch of a theme last started, for tests.</summary>
    internal Task Fetching { get; private set; } = Task.CompletedTask;

    private Task<ThemeAudioSource?> FetchAsync(string owner, string theme, CancellationToken cancellationToken)
    {
        var fetch = FetchForProfileAsync(owner, theme, cancellationToken);
        Fetching = fetch;
        return fetch;
    }

    // The theme's audio for the profile acting now, or none: a grant read while the profile changed is dropped, as the web's.
    private async Task<ThemeAudioSource?> FetchForProfileAsync(string owner, string theme, CancellationToken cancellationToken)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return null;
        }
        var identity = Identity();
        try
        {
            var grant = await _api.CreatePlaybackAsync(server.BaseUri, owner, theme, _output.Formats, cancellationToken);
            if (Identity() != identity)
            {
                return Dropped();
            }
            var audio = await _downloader.DownloadAsync(server.BaseUri, grant, _origins, cancellationToken);
            return Identity() == identity ? audio : Dropped();
        }
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // Never the grant or its address: the status, or the kind of failure.
            _logger.LogInformation("The theme music could not be played: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
    }

    // The profile changed while its theme was on its way: the change is taken now, before the session tells it, so the theme
    // stops and its next try does not go out as the next profile.
    private ThemeAudioSource? Dropped()
    {
        Apply();
        return null;
    }

    // The account, profile or server changed: stop at once, and ask the next one again.
    private void Reset()
    {
        _identity = Identity();
        _identityVersion++;
        _available = null;
        _origins = ThemeOrigins.Server;
        _readingCapability = false;
        Stop(immediate: true);
    }

    private (string? ServerId, string? ProfileId) Identity() => (_session.ActiveServer?.Id, _session.ProfileId);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TitlePageSettings.Shown))
        {
            Apply();
        }
    }

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(Apply);
}
