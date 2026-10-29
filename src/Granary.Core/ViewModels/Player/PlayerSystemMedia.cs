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

    private readonly PlayerViewModel _player;
    private readonly ISystemMediaControls _controls;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private SystemMediaState? _shown;
    private long _shownAt;
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
        _controls.Clear();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        if (_disposed)
        {
            return;
        }
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
        return new SystemMediaState(info.Title, info.Subtitle, info.Artwork, status, player.PositionSeconds, player.DurationSeconds,
            CanPlayPause: player.IsPlaying && player.CanPauseInRoom,
            CanSeek: Seeks && player.CanSeekInRoom,
            // In a Watch Party the room decides what plays next, so the player has no episodes around.
            CanGoPrevious: player.HasPreviousEpisode && !player.IsWatchParty,
            CanGoNext: player.HasNextEpisode && !player.IsWatchParty);
    }

    // The player itself can move: the room may still refuse a guest, and says so.
    private bool Seeks => _player.IsPlaying && _player.CanSeek;

    // Windows raises the buttons on a thread of its own.
    private void OnRequested(object? sender, SystemMediaRequest request) => _dispatcher.Post(() => Handle(request));

    // Each goes where the player's own button goes. In a Watch Party play, pause and the skips ask the room, which refuses a
    // guest it does not let with the web's words, as for the on-screen buttons; the controls show those buttons off.
    private void Handle(SystemMediaRequest request)
    {
        if (_disposed)
        {
            return;
        }
        var player = _player;
        switch (request.Button)
        {
            case SystemMediaButton.Play when player.IsPlaying && player.IsPaused:
            case SystemMediaButton.Pause when player.IsPlaying && !player.IsPaused:
                player.TogglePauseCommand.Execute(null);
                break;
            case SystemMediaButton.Rewind when Seeks:
                player.SkipBackCommand.Execute(null);
                break;
            case SystemMediaButton.FastForward when Seeks:
                player.SkipForwardCommand.Execute(null);
                break;
            case SystemMediaButton.Seek when Seeks && double.IsFinite(request.PositionSeconds):
                player.SeekTo(request.PositionSeconds);
                break;
            case SystemMediaButton.Previous when player.HasPreviousEpisode && !player.IsWatchParty:
                player.PlayPreviousEpisodeCommand.Execute(null);
                break;
            case SystemMediaButton.Next when player.HasNextEpisode && !player.IsWatchParty:
                player.PlayNextEpisodeCommand.Execute(null);
                break;
        }
    }
}
