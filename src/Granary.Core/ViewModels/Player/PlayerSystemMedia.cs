using System.ComponentModel;
using Granary.Core.Abstractions;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The player on Windows' media controls and keys (<see cref="ISystemMediaControls"/>), for as long as the player is open,
/// minimized to the bar too. The buttons are the player's own commands, so in a Watch Party they become requests to the
/// room, as the on-screen buttons do (Android's RoomTransportAuthority keeps the system media session from forking the
/// room's playback the same way).
/// </summary>
/// <remarks>
/// As Silo's apps offer them: play and pause (both apps), the skips by the profile's seconds (the Apple app's skip
/// commands with <c>videoSystemControls</c> intervals, the web's <c>seekbackward</c>/<c>seekforward</c> with the profile's
/// seconds), a place on the timeline (the Apple app's <c>changePlaybackPositionCommand</c>), and the episodes around this
/// one where the player has them (the Apple remote session's next, while there is a next episode). There is no Stop, as
/// in the apps' video players.
/// </remarks>
public sealed class PlayerSystemMedia : IDisposable
{
    /// <summary>How often the timeline follows playback on its own: Windows carries the position on in between (the Apple app pushes every 2 s too).</summary>
    public static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(2);

    // A position this far from where the last one shown has played on to is a jump, a seek, and shows at once.
    private const double JumpSeconds = 3;

    // What the controls show is read from these; the player's other changes (its menus, the next-up card) leave them alone.
    private static readonly HashSet<string> Shown =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.IsBuffering), nameof(PlayerViewModel.HasLeft),
        nameof(PlayerViewModel.PositionSeconds), nameof(PlayerViewModel.DurationSeconds), nameof(PlayerViewModel.CanSeek),
        nameof(PlayerViewModel.MediaInfo), nameof(PlayerViewModel.HasPreviousEpisode), nameof(PlayerViewModel.HasNextEpisode),
        nameof(PlayerViewModel.IsWatchParty), nameof(PlayerViewModel.CanSeekInRoom), nameof(PlayerViewModel.CanPauseInRoom),
    ];

    private readonly PlayerViewModel _player;
    private readonly ISystemMediaControls _controls;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private SystemMediaState? _shown;
    private long _shownAt;
    private bool _cleared;
    private bool _disposed;

    public PlayerSystemMedia(PlayerViewModel player, ISystemMediaControls controls, IUiDispatcher dispatcher, TimeProvider time)
    {
        _player = player;
        _controls = controls;
        _dispatcher = dispatcher;
        _time = time;
        _player.PropertyChanged += OnPlayerChanged;
        _controls.Requested += OnRequested;
        Update();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _player.PropertyChanged -= OnPlayerChanged;
        _controls.Requested -= OnRequested;
        if (!_cleared)
        {
            _controls.Clear();
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || Shown.Contains(e.PropertyName))
        {
            Update();
        }
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }
        // Left, nothing plays here any more: off the controls at once, not when the page goes.
        if (_player.HasLeft)
        {
            if (!_cleared)
            {
                _cleared = true;
                _shown = null;
                _controls.Clear();
            }
            return;
        }
        _cleared = false;
        var state = Read();
        if (state == _shown || (_shown is { } shown && state == shown with { PositionSeconds = state.PositionSeconds } && !TimelineDue(shown, state)))
        {
            return;
        }
        _shown = state;
        _shownAt = _time.GetTimestamp();
        _controls.Show(state);
    }

    // Only the position moved: shown again once the interval is over, or at once when it jumped.
    private bool TimelineDue(SystemMediaState shown, SystemMediaState state)
    {
        double elapsed = _time.GetElapsedTime(_shownAt).TotalSeconds;
        double expected = shown.PositionSeconds + (shown.Status == SystemMediaStatus.Playing ? elapsed : 0);
        return elapsed >= TimelineInterval.TotalSeconds || Math.Abs(state.PositionSeconds - expected) > JumpSeconds;
    }

    private SystemMediaState Read()
    {
        var player = _player;
        var status = player.Phase switch
        {
            PlayerPhase.Preparing => SystemMediaStatus.Changing,
            PlayerPhase.Playing => player.IsPaused ? SystemMediaStatus.Paused : SystemMediaStatus.Playing,
            _ => SystemMediaStatus.Stopped,
        };
        var info = player.MediaInfo;
        // An unknown or endless length is none: the controls take only finite times.
        double duration = double.IsFinite(player.DurationSeconds) && player.DurationSeconds > 0 ? player.DurationSeconds : 0;
        double position = double.IsFinite(player.PositionSeconds) ? Math.Max(0, player.PositionSeconds) : 0;
        return new SystemMediaState(info.Title, info.Subtitle, info.Artwork, status, position, duration,
            CanPlayPause, CanSeek, CanGoPrevious, CanGoNext);
    }

    // What the buttons may do now, the same for what the controls show and for a press: Windows shows a change a moment
    // later, and a press it queued before (a room that took a guest's pause away, a player just left) must not act on the
    // state that was. In a Watch Party play and pause are the host's and those of a guest the room lets, seeking the host's.
    private bool CanPlayPause => !_player.HasLeft && _player.IsPlaying && _player.CanPauseInRoom;

    private bool CanSeek => !_player.HasLeft && _player.IsPlaying && _player.CanSeek && _player.CanSeekInRoom;

    // Settled, not loading nor buffering or replanning, so presses start one load, not one each; and in a Watch Party the
    // room decides what plays next, so the player has no episodes around.
    private bool CanChangeEpisode => !_player.HasLeft && _player.IsPlaying && !_player.IsBuffering && !_player.IsWatchParty;

    private bool CanGoPrevious => CanChangeEpisode && _player.HasPreviousEpisode;

    private bool CanGoNext => CanChangeEpisode && _player.HasNextEpisode;

    // Windows raises the buttons on a thread of its own.
    private void OnRequested(object? sender, SystemMediaRequest request) => _dispatcher.Post(() => Handle(request));

    // Each goes where the player's own button goes, so in a Watch Party play, pause and the skips ask the room.
    private void Handle(SystemMediaRequest request)
    {
        if (_disposed)
        {
            return;
        }
        var player = _player;
        switch (request.Button)
        {
            case SystemMediaButton.Play when CanPlayPause && player.IsPaused:
            case SystemMediaButton.Pause when CanPlayPause && !player.IsPaused:
                player.TogglePauseCommand.Execute(null);
                break;
            case SystemMediaButton.Rewind when CanSeek:
                player.SkipBackCommand.Execute(null);
                break;
            case SystemMediaButton.FastForward when CanSeek:
                player.SkipForwardCommand.Execute(null);
                break;
            case SystemMediaButton.Seek when CanSeek && double.IsFinite(request.PositionSeconds):
                player.SeekTo(request.PositionSeconds);
                break;
            case SystemMediaButton.Previous when CanGoPrevious:
                player.PlayPreviousEpisodeCommand.Execute(null);
                break;
            case SystemMediaButton.Next when CanGoNext:
                player.PlayNextEpisodeCommand.Execute(null);
                break;
        }
    }
}
