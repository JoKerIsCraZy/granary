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
/// on-screen controls and keyboard. <see cref="StartAsync"/> starts a <see cref="PlayRequest"/>.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private PlayRequest? _request;

    public PlayerViewModel(IPlaybackEngine engine, IUiDispatcher dispatcher, INavigationService navigation)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _navigation = navigation;
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

    /// <summary>Waiting for data; show a spinner over the picture.</summary>
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
    public double SkipBackSeconds { get; private set; } = 10;

    /// <summary>Seconds the forward skip (button, Right key) jumps; the profile's <c>player.video_skip_forward_seconds</c>.</summary>
    public double SkipForwardSeconds { get; private set; } = 30;

    /// <summary>Starts <paramref name="request"/>; the page calls it once when it is shown.</summary>
    public Task StartAsync(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        ErrorMessage = "";
        CanRetry = false;
        Phase = PlayerPhase.Preparing;
        if (request.DirectSource is { } direct)
        {
            StartDirect(direct);
            return Task.CompletedTask;
        }
        // Server playback follows (Phase 3c).
        Fail("Playback from the server is not available yet.", canRetry: false);
        return Task.CompletedTask;
    }

    /// <summary>Moves to <paramref name="seconds"/> of the title (the seek bar), within its length.</summary>
    public void SeekTo(double seconds)
    {
        double target = Math.Max(0, seconds);
        if (DurationSeconds > 0)
        {
            target = Math.Min(target, DurationSeconds);
        }
        _engine.Seek(TimeSpan.FromSeconds(target));
    }

    /// <summary>Raises or lowers the volume by <paramref name="delta"/> percentage points (Up and Down keys).</summary>
    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    [RelayCommand]
    private void TogglePause() => _engine.SetPaused(!_engine.Current.IsPaused);

    [RelayCommand]
    private void SkipBack() => SeekTo(PositionSeconds - SkipBackSeconds);

    [RelayCommand]
    private void SkipForward() => SeekTo(PositionSeconds + SkipForwardSeconds);

    [RelayCommand]
    private void ToggleMute() => _engine.SetMuted(!IsMuted);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _request is { } request ? StartAsync(request) : Task.CompletedTask;

    /// <summary>Stops playback and leaves the player (Back, Esc outside full screen).</summary>
    [RelayCommand]
    private void Close()
    {
        _engine.Stop();
        if (_navigation.CanGoBack)
        {
            _navigation.GoBack();
        }
        else
        {
            _navigation.ResetTo(AppRoute.Home);
        }
    }

    partial void OnVolumeChanged(double value)
    {
        if (Math.Abs(_engine.Current.Volume - value) > 0.01)
        {
            _engine.SetVolume(value);
        }
    }

    private void StartDirect(string text)
    {
        text = text.Trim().Trim('"');
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

    private void OnStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() =>
    {
        IsPaused = state.IsPaused;
        IsBuffering = state.IsBuffering;
        PositionSeconds = state.Position.TotalSeconds;
        DurationSeconds = state.Duration?.TotalSeconds ?? 0;
        IsMuted = state.IsMuted;
        Volume = state.Volume;
        CanSeek = state.IsLoaded && state.HasStarted;
        if (state.HasStarted && Phase == PlayerPhase.Preparing)
        {
            Phase = PlayerPhase.Playing;
        }
    });

    private void OnEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
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
        _engine.StateChanged -= OnStateChanged;
        _engine.Ended -= OnEnded;
    }
}
