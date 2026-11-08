using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly PlaybackStops _stops;
    private IDisposable? _playing;
    private readonly TimeProvider _time;
    private readonly long _created;
    private readonly IUpNextSource? _upNext;
    private readonly AutoPlayStreak _streak;
    private readonly ILogger _logger;
    private readonly global::Granary.Core.WatchParty.WatchPartySession? _watchParty;
    private readonly EpisodeSpoilerSettings? _spoilers;
    // This PC's own player choices; without them, as in tests that leave them out, the defaults hold and nothing is kept.
    private readonly IPlaybackDevicePreferences? _devicePreferences;
    // A seek's target counts as the position for this long, until the engine reports it: quick skips add up.
    private const long SeekTargetMilliseconds = 3000;

    private PlayRequest? _request;
    private IServerPlayback? _playback;
    private PlaybackSnapshot _engineState = PlaybackSnapshot.Idle;
    private double? _seekTarget;
    private long _seekTargetAt;

    public PlayerViewModel(
        IPlaybackEngine engine,
        IUiDispatcher dispatcher,
        INavigationService navigation,
        Func<IServerPlayback> serverPlaybacks,
        PlaybackStops stops,
        TimeProvider? time = null,
        IUpNextSource? upNext = null,
        AutoPlayStreak? streak = null,
        IProfileSettings? settings = null,
        ILogger<PlayerViewModel>? logger = null,
        Auth.AccountPermissions? permissions = null,
        global::Granary.Core.WatchParty.WatchPartySession? watchParty = null,
        IConfirmation? confirmation = null,
        EpisodeSpoilerSettings? spoilers = null,
        IPlaybackDevicePreferences? devicePreferences = null)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _navigation = navigation;
        _serverPlaybacks = serverPlaybacks;
        _stops = stops;
        _time = time ?? TimeProvider.System;
        _created = _time.GetTimestamp();
        _upNext = upNext;
        _streak = streak ?? new AutoPlayStreak();
        _settings = settings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _permissions = permissions;
        _watchParty = watchParty;
        _spoilers = spoilers;
        _devicePreferences = devicePreferences;
        ShowsTimeLeft = devicePreferences?.TimeLeft == true;
        Party =InitializeWatchParty(confirmation);
        Look.Edited += OnLookEdited;
        InitializeMarkerEdit();
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

    /// <summary>The viewer left the player (<see cref="Leave"/>): nothing plays here any more, until a new start.</summary>
    [ObservableProperty]
    public partial bool HasLeft { get; private set; }

    /// <summary>The movie, or the series of an episode.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"S1 · E5 — Episode title" for an episode, the year for a movie; may be empty.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    /// <summary>What Windows' media controls show for the title: its lines as Silo's apps write them there, and its picture.</summary>
    [ObservableProperty]
    public partial PlaybackMediaInfo MediaInfo { get; private set; } = PlaybackMediaInfo.None;

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

    /// <summary>What plays has no picture of its own (<see cref="PlaybackSnapshot.IsAudioOnly"/>), so the display may sleep.</summary>
    [ObservableProperty]
    public partial bool IsAudioOnly { get; private set; }

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

    /// <summary>The rows of the audio menu, the one that plays checked; empty without a server title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioTracks), nameof(CanChooseAudio))]
    public partial IReadOnlyList<AudioTrackOption> AudioTracks { get; private set; } = [];

    /// <summary>The audio button shows once there is a track; as on the web player, it opens with two or more.</summary>
    public bool HasAudioTracks => AudioTracks.Count > 0;

    public bool CanChooseAudio => AudioTracks.Count > 1;

    /// <summary>The rows of the subtitle menu, Off first, the one that shows checked; empty until a server title has a plan.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SubtitleTrackOption> SubtitleTracks { get; private set; } = [];

    /// <summary>The subtitle delay in milliseconds, positive later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleDelayText), nameof(CanDelayEarlier), nameof(CanDelayLater), nameof(CanResetSubtitleDelay))]
    public partial int SubtitleDelayMs { get; private set; }

    /// <summary>"0 ms", "+300 ms", "−1200 ms" (web: formatDelay).</summary>
    public string SubtitleDelayText => SubtitleDelayMs == 0
        ? "0 ms"
        : (SubtitleDelayMs > 0 ? "+" : "\u2212") + Math.Abs(SubtitleDelayMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";

    // As on the web, the delay is for a subtitle that shows.
    public bool CanDelayEarlier => IsSubtitleOn && SubtitleDelayMs > -PlaybackPreferencesReader.MaxSubtitleDelayMs;

    public bool CanDelayLater => IsSubtitleOn && SubtitleDelayMs < PlaybackPreferencesReader.MaxSubtitleDelayMs;

    public bool CanResetSubtitleDelay => IsSubtitleOn && SubtitleDelayMs != 0;

    /// <summary>Moves subtitles by <paramref name="deltaMs"/>; the menu steps by 100 ms.</summary>
    public void ChangeSubtitleDelay(int deltaMs)
    {
        if (_playback is { } playback)
        {
            playback.SetSubtitleDelay(SubtitleDelayMs + deltaMs);
            SubtitleDelayMs = playback.SubtitleDelayMs;
        }
    }

    public void ResetSubtitleDelay() => ChangeSubtitleDelay(-SubtitleDelayMs);

    /// <summary>Starts <paramref name="request"/>; the page calls it once when it is shown, Retry again.</summary>
    public Task StartAsync(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A title the viewer started (or retried) is the viewer's doing: the pass-out gate opens again, and a sleep timer
        // that ran out while the one before loaded does not pause it.
        _streak.Reset();
        _sleepPausePending = false;
        return PlayAsync(request);
    }

    // A start of the player's own: the next episode after a countdown.
    private async Task PlayAsync(PlayRequest request)
    {
        EndWatchParty();
        _request = request;
        // A seek on its way belongs to the title before.
        _seekTarget = null;
        ErrorMessage = "";
        CanRetry = false;
        Phase = PlayerPhase.Preparing;
        // Nothing to seek in until the new stream plays, also before the engine first reports the new load.
        CanSeek = false;
        MediaInfo = PlaybackMediaInfo.None;
        // Back only now that the title before is cleared, so what follows the player (the taskbar, Windows' media
        // controls, the Discord status) does not show that one again for a moment.
        HasLeft = false;
        LeavePlayback();
        ResetFill();
        ResetLook();
        ShowSleepTimer();
        if (request.DirectSource is { } direct)
        {
            StartDirect(direct);
            return;
        }
        var playback = _serverPlaybacks();
        _playback = playback;
        _playing = _stops.Playing();
        HasSubtitleMenu = true;
        ReadMarkerPermission();
        playback.StatusChanged += OnPlaybackStatusChanged;
        playback.TracksChanged += OnTracksChanged;
        playback.MarkersChanged += OnMarkersChanged;
        playback.TrickplayChanged += OnTrickplayChanged;
        playback.NoticeRaised += OnNoticeRaised;
        playback.PlanChangeChanged += OnPlaybackPlanChanged;
        FollowSubtitleSync(playback);
        BeginWatchParty(request);
        // The room was over, or had moved on, before the player bound to it: the player left already.
        if (HasLeft)
        {
            return;
        }
        await playback.StartAsync(request, CancellationToken.None);
    }

    /// <summary>The viewer's pick in the audio menu: it plays at once and is saved for the series.</summary>
    public void SelectAudio(int ordinal) => _playback?.SelectAudio(ordinal);

    /// <summary>
    /// Keeps subtitles clear of the controls: raised by <paramref name="fraction"/> of the picture's height while they
    /// show, back down (0) when they fade.
    /// </summary>
    public void LiftSubtitles(double fraction) => _engine.SetSubtitleLift(fraction);

    /// <summary>The viewer's pick in the subtitle menu, null for Off: it shows at once and is saved for the series.</summary>
    public void SelectSubtitle(string? trackId) => _playback?.SelectSubtitle(trackId);

    /// <summary>The viewer moves to <paramref name="seconds"/> of the title (the seek bar, the skips), within its length.</summary>
    public void SeekTo(double seconds)
    {
        _streak.Reset();
        _nextUp.Sought(Now);
        ShowNextUp();
        Seek(seconds);
    }

    // A seek of the player's own, as when a marked part skips itself. In a Watch Party it is asked of the room.
    private void Seek(double seconds)
    {
        if (RequestRoomSeek(Math.Max(0, seconds)))
        {
            return;
        }
        SeekLocally(seconds);
    }

    // Moves the player itself: the viewer's seek, or the room's.
    private void SeekLocally(double seconds)
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
        if (HasLeft)
        {
            return;
        }
        HasLeft = true;
        EndWatchParty();
        LeavePlayback();
        _engine.Stop();
        // As Apple's player ends its timer with the player.
        StopSleepTimer();
    }

    /// <summary>Raises or lowers the volume by <paramref name="delta"/> percentage points (Up and Down keys).</summary>
    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    // Only what plays pauses: a pause set while preparing or after a failure would hold the next load at its start.
    [RelayCommand]
    private void TogglePause()
    {
        if (IsPlaying)
        {
            _streak.Reset();
            if (RequestRoomPlayPause())
            {
                return;
            }
            if (_engine.Current.IsPaused)
            {
                _engine.SetPaused(false);
            }
            else
            {
                Pause();
            }
        }
    }

    /// <summary>
    /// A trailer opens over the page while this plays in the bar (<see cref="Detail.TrailerOverlayViewModel"/>): it pauses,
    /// so the two do not play over each other. A Watch Party's playback is the room's and goes on. True when it paused.
    /// </summary>
    public bool PauseForTrailer()
    {
        if (IsWatchParty || !IsPlaying || _engine.Current.IsPaused)
        {
            return false;
        }
        Pause();
        return true;
    }

    // A pause also stops the next-up card's countdown.
    private void Pause()
    {
        _nextUp.Paused(Now);
        ShowNextUp();
        _engine.SetPaused(true);
    }

    [RelayCommand]
    private void SkipBack() => SeekTo(SkipOrigin - SkipBackSeconds);

    [RelayCommand]
    private void SkipForward() => SeekTo(SkipOrigin + SkipForwardSeconds);

    // Where a skip goes from: a seek on its way, else the position.
    private double SkipOrigin =>
        _party?.PendingSeekSeconds ?? (_seekTarget is { } target && Environment.TickCount64 - _seekTargetAt < SeekTargetMilliseconds ? target : PositionSeconds);

    [RelayCommand]
    private void ToggleMute() => _engine.SetMuted(!IsMuted);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _request is { } request ? StartAsync(request) : Task.CompletedTask;

    /// <summary>Stops playback and leaves the player (Exit, Esc outside full screen, the end of a movie), full or minimized.</summary>
    [RelayCommand]
    private void Close()
    {
        Leave();
        _navigation.LeavePlayer();
    }

    // A value the engine does not have yet is the viewer's change: it also ends a mute, as on the web player. One the engine
    // reported is not, even when the engine has moved on since: a new mpv says its own volume before it gets the viewer's.
    partial void OnVolumeChanged(double value)
    {
        if (!_showingEngineState && Math.Abs(_engine.Current.Volume - value) > 0.01)
        {
            if (_engine.Current.IsMuted)
            {
                _engine.SetMuted(false);
            }
            _engine.SetVolume(value);
        }
    }

    // Ends the server playback, if any. Its stop reads the final position before the engine stops; closing the app
    // waits for it (PlaybackStops), leaving the page does not.
    private void LeavePlayback()
    {
        ResetNextUp();
        // A file played directly too: what it held is not the next title's.
        ResetBuffered();
        if (_playback is not { } playback)
        {
            return;
        }
        _playback = null;
        playback.StatusChanged -= OnPlaybackStatusChanged;
        playback.TracksChanged -= OnTracksChanged;
        playback.MarkersChanged -= OnMarkersChanged;
        playback.TrickplayChanged -= OnTrickplayChanged;
        playback.NoticeRaised -= OnNoticeRaised;
        playback.PlanChangeChanged -= OnPlaybackPlanChanged;
        ForgetSubtitleSync(playback);
        PlanChange = PlanChange.None;
        ResetNotice();
        AudioTracks = [];
        HasSubtitleMenu = false;
        ResetSubtitles();
        ResetMarkerEdit();
        ResetMarkers();
        ResetChapters();
        var playing = _playing;
        _playing = null;
        _stops.Track(EndAsync(playback, playing));

        // It counts as playing until its stop ended: the server's echo of its progress is this app's own.
        static async Task EndAsync(IServerPlayback playback, IDisposable? playing)
        {
            try
            {
                await playback.StopAsync();
            }
            finally
            {
                try
                {
                    await playback.DisposeAsync();
                }
                finally
                {
                    playing?.Dispose();
                }
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
        MediaInfo = new PlaybackMediaInfo(Title, "", null);
        _engine.Load(source);
        // mpv keeps a pause across files: a file opened plays.
        _engine.SetPaused(false);
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
        MediaInfo = playback.MediaInfo;
        SubtitleDelayMs = playback.SubtitleDelayMs;
        SkipBackSeconds = playback.Preferences.SkipBackSeconds;
        SkipForwardSeconds = playback.Preferences.SkipForwardSeconds;
        ShowSettings();
        ShowSleepTimer();
        switch (status.Phase)
        {
            case ServerPlaybackPhase.Preparing:
                Phase = PlayerPhase.Preparing;
                break;
            case ServerPlaybackPhase.Playing or ServerPlaybackPhase.Reconnecting:
                Phase = PlayerPhase.Playing;
                BeginNextUp(playback);
                PauseIfSleptWhileLoading();
                break;
            case ServerPlaybackPhase.Failed:
                Fail(status.Message, status.CanRetry);
                break;
            case ServerPlaybackPhase.Ended:
                Phase = PlayerPhase.Ended;
                EndSleepAtEnd();
                EndNextUp();
                break;
        }
        ShowEngineState();
    }

    private void OnTracksChanged(object? sender, EventArgs e)
    {
        if (sender == _playback && _playback is { } playback)
        {
            AudioTracks = AudioTrackOption.For(playback.AudioTracks, playback.AudioTrack);
            TakeSubtitles(playback);
            ShowMarkerEdit();
            TakeMarkers(playback);
            TakeChapters(playback);
            ShowSettings();
        }
    }

    private void OnStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() =>
    {
        // The engine raises from mpv's event thread, a timer's (its cache reads) and the caller's of a load: a snapshot
        // can arrive after a newer one, and would take the player back to a state it has left (a stopped file loaded
        // again, the position back). The marker prompts and a Watch Party's view of the player read this one too.
        if (state.Sequence < _engineState.Sequence)
        {
            return;
        }
        _engineState = state;
        ShowEngineState();
        // A direct source plays as soon as the engine does; a server playback says so itself.
        if (_playback is null && state.HasStarted && Phase == PlayerPhase.Preparing)
        {
            Phase = PlayerPhase.Playing;
            PauseIfSleptWhileLoading();
        }
    });

    // Set while the volume shown follows the engine's (OnVolumeChanged). On the UI thread.
    private bool _showingEngineState;

    // Server titles are shown in their own time: the stream may start later than the title (8.6).
    private void ShowEngineState()
    {
        var state = _engineState;
        double offset = _playback?.TimelineOffsetSeconds ?? 0;
        IsPaused = state.IsPaused;
        IsBuffering = state.IsBuffering || _playback?.Status.Phase == ServerPlaybackPhase.Reconnecting;
        IsAudioOnly = state.IsAudioOnly;
        PositionSeconds = state.Position.TotalSeconds + offset;
        if (_seekTarget is { } target && Math.Abs(PositionSeconds - target) < 1.5)
        {
            _seekTarget = null;
        }
        DurationSeconds = _playback?.DurationSeconds ?? (state.Duration is { } duration ? duration.TotalSeconds + offset : 0);
        IsMuted = state.IsMuted;
        _showingEngineState = true;
        Volume = state.Volume;
        _showingEngineState = false;
        CanSeek = state.IsLoaded && state.HasStarted && (_playback is null || _playback.Status.Phase == ServerPlaybackPhase.Playing);
        FollowBuffered(state, offset);
        FollowMarkers(state);
        ShowMarkerEdit();
        FollowChapters();
        FollowNextUp();
        // During a plan change the spinner waits for the picture to stop.
        FollowWait();
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
                EndSleepAtEnd();
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
        ResetNextUp();
        StopSleepTicks();
        StopPlaybackInfo();
        _syncFeedback?.Dispose();
        _engine.StateChanged -= OnStateChanged;
        _engine.Ended -= OnEnded;
        StopPlanChangeTimers();
    }
}
