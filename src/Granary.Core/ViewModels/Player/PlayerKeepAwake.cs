using System.ComponentModel;
using Granary.Core.Abstractions;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Keeps the display on while the player plays (<see cref="IKeepAwake"/>), minimized to the bar and in the mini view too, as
/// Android's <c>PlayerScreen</c> holds FLAG_KEEP_SCREEN_ON (<c>keepScreenAwake</c>): while a title loads, plays or buffers,
/// not while it is paused, and not once it ended, failed or the player was left. Sound without a picture keeps only the
/// system awake, as Android's audiobook player keeps no screen on and the Apple app's
/// <c>AetherPlaybackController.shouldPreventDisplaySleep</c> lets the display sleep for music.
/// </summary>
/// <remarks>
/// mpv's own <c>stop-screensaver</c> asks Windows through mpv's window, and in composition mode mpv has none, so Granary asks
/// itself. While paused, Windows' own timeout applies again.
/// </remarks>
public sealed class PlayerKeepAwake : IDisposable
{
    // What is held is read from these; position ticks and the player's other changes leave it alone.
    private static readonly HashSet<string> Followed =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.HasLeft),
        nameof(PlayerViewModel.IsAudioOnly),
    ];

    private readonly PlayerViewModel _player;
    private readonly IKeepAwake _keepAwake;
    private KeepAwakeState _held;
    private bool _disposed;

    public PlayerKeepAwake(PlayerViewModel player, IKeepAwake keepAwake)
    {
        _player = player;
        _keepAwake = keepAwake;
        _player.PropertyChanged += OnPlayerChanged;
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
        Hold(KeepAwakeState.None);
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || Followed.Contains(e.PropertyName))
        {
            Update();
        }
    }

    private void Update()
    {
        if (!_disposed)
        {
            Hold(Wanted(_player));
        }
    }

    // A title that loads is about to play, whatever the one before was; buffering is playing, as on Android.
    private static KeepAwakeState Wanted(PlayerViewModel player) => player.HasLeft ? KeepAwakeState.None : player.Phase switch
    {
        PlayerPhase.Preparing => KeepAwakeState.Display,
        PlayerPhase.Playing when player.IsPaused => KeepAwakeState.None,
        PlayerPhase.Playing => player.IsAudioOnly ? KeepAwakeState.System : KeepAwakeState.Display,
        _ => KeepAwakeState.None,
    };

    // Asked only when it differs from what Windows holds; one it refused is asked again with the player's next change.
    private void Hold(KeepAwakeState state)
    {
        if (state != _held && _keepAwake.Set(state))
        {
            _held = state;
        }
    }
}
