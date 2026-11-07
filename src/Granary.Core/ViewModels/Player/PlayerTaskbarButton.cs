using System.ComponentModel;
using Granary.Core.Abstractions;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The player's Play/Pause in the taskbar preview (<see cref="ITaskbarPlayButton"/>), from a title's load until the player is
/// left, minimized too. A press is the player's own Play/Pause, so in a Watch Party it asks the room, as Windows' media
/// controls do (<see cref="PlayerSystemMedia"/>, whose rule says when it works).
/// </summary>
public sealed class PlayerTaskbarButton : IDisposable
{
    // What the button shows is read from these; position ticks and the player's other changes leave it alone.
    private static readonly HashSet<string> Shown =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.HasLeft),
        nameof(PlayerViewModel.IsWatchParty), nameof(PlayerViewModel.CanPauseInRoom),
    ];

    private readonly PlayerViewModel _player;
    private readonly ITaskbarPlayButton _button;
    private readonly IUiDispatcher _dispatcher;
    private TaskbarPlayButtonState? _shown;
    private bool _disposed;

    public PlayerTaskbarButton(PlayerViewModel player, ITaskbarPlayButton button, IUiDispatcher dispatcher)
    {
        _player = player;
        _button = button;
        _dispatcher = dispatcher;
        _player.PropertyChanged += OnPlayerChanged;
        _button.Pressed += OnPressed;
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
        _button.Pressed -= OnPressed;
        Hide();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || Shown.Contains(e.PropertyName))
        {
            Update();
        }
    }

    // Shown again only when it changes: the taskbar is another process.
    private void Update()
    {
        if (_disposed)
        {
            return;
        }
        // Left, nothing plays here any more: out of the preview at once, not when the page goes.
        if (_player.HasLeft)
        {
            Hide();
            return;
        }
        var state = new TaskbarPlayButtonState(_player.IsPaused, PlayerSystemMedia.PlayPauseWorks(_player));
        if (state == _shown)
        {
            return;
        }
        _shown = state;
        _button.Show(state);
    }

    private void Hide()
    {
        if (_shown is not null)
        {
            _shown = null;
            _button.Hide();
        }
    }

    // Raised inside the window's message: handled once that has returned, so the taskbar is not called back from within it.
    private void OnPressed(object? sender, EventArgs e) => _dispatcher.Post(Press);

    // As the player's own button, on the state as it is now: a room may have taken a guest's pause away since it showed.
    private void Press()
    {
        if (!_disposed && PlayerSystemMedia.PlayPauseWorks(_player))
        {
            _player.TogglePauseCommand.Execute(null);
        }
    }
}
