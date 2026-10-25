using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

public enum PlayerPhase
{
    /// <summary>Asking the server for a plan and loading it; nothing to show yet.</summary>
    Preparing,

    Playing,

    /// <summary>Playback could not start or broke off; <see cref="PlayerViewModel.ErrorMessage"/> says why.</summary>
    Failed,

    /// <summary>The title played to its end.</summary>
    Ended,
}

/// <summary>
/// The player page: what plays, the transport state in the title's own time, and the commands of the
/// on-screen controls and keyboard. <see cref="StartAsync"/> starts a <see cref="PlayRequest"/>: a server
/// title through an <see cref="IServerPlayback"/>, or a local file or URL straight into the engine.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private readonly Func<IServerPlayback> _serverPlaybacks;
    // A seek's target counts as the position for this long, until the engine reports it: quick skips add up.
    private const long SeekTargetMilliseconds = 3000;

    private PlayRequest? _request;
    private IServerPlayback? _playback;
    private PlaybackSnapshot _engineState = PlaybackSnapshot.Idle;
    private double? _seekTarget;
    private long _seekTargetAt;
    private bool _left;

    public PlayerViewModel(IPlaybackEngine engine, IUiDispatcher dispatcher, INavigationService navigation, Func<IServerPlayback> serverPlaybacks)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _navigation = navigation;
        _serverPlaybacks = serverPlaybacks;
        _engine.StateChanged += OnStateChanged;
        _engine.Ended += OnEnded;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparing), nameof(IsPlaying), nameof(IsFailed), nameof(IsEnded))]
    public partial PlayerPhase Phase { get; private set; } = PlayerPhase.Preparing;

    public bool IsPreparing => Phase == PlayerPhase.Preparing;

    public bool IsPlaying => Phase == PlayerPhase.Playing;

    public bool IsFailed => Phase == PlayerPhase.Failed;

    public bool IsEnded => Phase == PlayerPhase.Ended;

    /// <summary>The movie, or the series of an episode.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"S01E05 · Episode title" for an episode, the year for a movie; may be empty.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>Whether <see cref="RetryCommand"/> can help after a failure.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// <summary>Waiting for data or for the connection to come back; show a spinner over the picture.</summary>
    [ObservableProperty]
    public partial bool IsBuffering { get; private set; }

    /// <summary>Position in the title's own time (source time), in seconds.</summary>
    [ObservableProperty]
    public partial double PositionSeconds { get; private set; }

    /// <summary>The title's length in seconds; 0 while unknown.</summary>
    [ObservableProperty]
    public partial double DurationSeconds { get; private set; }

    /// <summary>Whether the seek bar and skip buttons work right now.</summary>
    [ObservableProperty]
    public partial bool CanSeek { get; private set; }

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    /// <summary>Seconds the back skip (button, Left key) jumps; the profile's <c>player.video_skip_back_seconds</c>.</summary>
    [ObservableProperty]
    public partial double SkipBackSeconds { get; private set; } = PlaybackPreferences.Default.SkipBackSeconds;

    /// <summary>Seconds the forward skip (button, Right key) jumps; the profile's <c>player.video_skip_forward_seconds</c>.</summary>
    [ObservableProperty]
    public partial double SkipForwardSeconds { get; private set; } = PlaybackPreferences.Default.SkipForwardSeconds;

    /// <summary>Starts <paramref name="request"/>; the page calls it once when it is shown, Retry again.</summary>
    public async Task StartAsync(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _left = false;
        ErrorMessage = "";
        CanRetry = false;
        Phase = PlayerPhase.Preparing;
        LeavePlayback();
        if (request.DirectSource is { } direct)
        {
            StartDirect(direct);
            return;
        }
        var playback = _serverPlaybacks();
        _playback = playback;
        playback.StatusChanged += OnPlaybackStatusChanged;
        await playback.StartAsync(request, CancellationToken.None);
    }

    /// <summary>Moves to <paramref name="seconds"/> of the title (the seek bar), within its length.</summary>
    public void SeekTo(double seconds)
    {
        double target = Math.Max(0, seconds);
        if (DurationSeconds > 0)
        {
            target = Math.Min(target, DurationSeconds);
        }
        _seekTarget = target;
        _seekTargetAt = Environment.TickCount64;
        if (_playback is { } playback)
        {
            playback.Seek(target);
        }
        else
        {
            _engine.Seek(TimeSpan.FromSeconds(target));
        }
    }

    /// <summary>
    /// Ends playback without navigating: the server session gets its final position and the engine stops. The page
    /// calls it when it is left by any way (Back, Alt+Left, the mouse's back button, a shortcut); safe to repeat.
    /// </summary>
    public void Leave()
    {
        if (_left)
        {
            return;
        }
        _left = true;
        LeavePlayback();
        _engine.Stop();
    }

    /// <summary>Raises or lowers the volume by <paramref name="delta"/> percentage points (Up and Down keys).</summary>
    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    [RelayCommand]
    private void TogglePause() => _engine.SetPaused(!_engine.Current.IsPaused);

    [RelayCommand]
    private void SkipBack() => SeekTo(SkipOrigin - SkipBackSeconds);

    [RelayCommand]
    private void SkipForward() => SeekTo(SkipOrigin + SkipForwardSeconds);

    // Where a skip goes from: a seek on its way, else the position.
    private double SkipOrigin =>
        _seekTarget is { } target && Environment.TickCount64 - _seekTargetAt < SeekTargetMilliseconds ? target : PositionSeconds;

    [RelayCommand]
    private void ToggleMute() => _engine.SetMuted(!IsMuted);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _request is { } request ? StartAsync(request) : Task.CompletedTask;

    /// <summary>Stops playback and leaves the player (Back, Esc outside full screen).</summary>
    [RelayCommand]
    private void Close()
    {
        Leave();
        if (_navigation.CanGoBack)
        {
            _navigation.GoBack();
        }
        else
        {
            _navigation.ResetTo(AppRoute.Home);
        }
    }

    // A value the engine does not have yet is the viewer's change: it also ends a mute, as on the web player.
    partial void OnVolumeChanged(double value)
    {
        if (Math.Abs(_engine.Current.Volume - value) > 0.01)
        {
            if (_engine.Current.IsMuted)
            {
                _engine.SetMuted(false);
            }
            _engine.SetVolume(value);
        }
    }

    // Ends the server playback, if any. Its stop reads the final position before the engine stops.
    private void LeavePlayback()
    {
        if (_playback is not { } playback)
        {
            return;
        }
        _playback = null;
        playback.StatusChanged -= OnPlaybackStatusChanged;
        _ = EndAsync(playback);

        static async Task EndAsync(IServerPlayback playback)
        {
            try
            {
                await playback.StopAsync();
            }
            finally
            {
                await playback.DisposeAsync();
            }
        }
    }

    private void StartDirect(string text)
    {
        text = text.Trim().Trim('"');
        // Opening a network path would make Windows sign in to that host with the user's account.
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            Fail("Network paths cannot be played; copy the file to this computer.", canRetry: false);
            return;
        }
        var source = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new MediaSource(uri, [])
            : MediaSource.Local(text);
        Title = Path.GetFileName(source.Url.IsFile ? source.Url.LocalPath : source.Url.AbsolutePath);
        Subtitle = "";
        _engine.Load(source);
    }

    private void Fail(string message, bool canRetry)
    {
        ErrorMessage = message;
        CanRetry = canRetry;
        Phase = PlayerPhase.Failed;
    }

    private void OnPlaybackStatusChanged(object? sender, ServerPlaybackStatus status)
    {
        if (sender != _playback || _playback is not { } playback)
        {
            return;
        }
        Title = playback.Title;
        Subtitle = playback.Subtitle;
        SkipBackSeconds = playback.Preferences.SkipBackSeconds;
        SkipForwardSeconds = playback.Preferences.SkipForwardSeconds;
        switch (status.Phase)
        {
            case ServerPlaybackPhase.Preparing:
                Phase = PlayerPhase.Preparing;
                break;
            case ServerPlaybackPhase.Playing or ServerPlaybackPhase.Reconnecting:
                Phase = PlayerPhase.Playing;
                break;
            case ServerPlaybackPhase.Failed:
                Fail(status.Message, status.CanRetry);
                break;
            case ServerPlaybackPhase.Ended:
                Phase = PlayerPhase.Ended;
                break;
        }
        ShowEngineState();
    }

    private void OnStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() =>
    {
        _engineState = state;
        ShowEngineState();
        // A direct source plays as soon as the engine does; a server playback says so itself.
        if (_playback is null && state.HasStarted && Phase == PlayerPhase.Preparing)
        {
            Phase = PlayerPhase.Playing;
        }
    });

    // Server titles are shown in their own time: the stream may start later than the title (8.6).
    private void ShowEngineState()
    {
        var state = _engineState;
        double offset = _playback?.TimelineOffsetSeconds ?? 0;
        IsPaused = state.IsPaused;
        IsBuffering = state.IsBuffering || _playback?.Status.Phase == ServerPlaybackPhase.Reconnecting;
        PositionSeconds = state.Position.TotalSeconds + offset;
        if (_seekTarget is { } target && Math.Abs(PositionSeconds - target) < 1.5)
        {
            _seekTarget = null;
        }
        DurationSeconds = _playback?.DurationSeconds ?? (state.Duration is { } duration ? duration.TotalSeconds + offset : 0);
        IsMuted = state.IsMuted;
        Volume = state.Volume;
        CanSeek = state.IsLoaded && state.HasStarted && (_playback is null || _playback.Status.Phase == ServerPlaybackPhase.Playing);
    }

    private void OnEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
        // A server playback decides itself what an end means (a rejected token is reloaded, not shown).
        if (_playback is not null)
        {
            return;
        }
        switch (ended.Reason)
        {
            case PlaybackEndReason.EndOfFile:
                Phase = PlayerPhase.Ended;
                break;
            case PlaybackEndReason.AuthenticationFailed:
                Fail("The server rejected the credentials.", canRetry: true);
                break;
            case PlaybackEndReason.Error:
                Fail($"Playback failed: {ended.Detail}", canRetry: true);
                break;
        }
    });

    public void Dispose()
    {
        Leave();
        _engine.StateChanged -= OnStateChanged;
        _engine.Ended -= OnEnded;
    }
}
