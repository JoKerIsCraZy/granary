using System.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The player's Previous episode, Play/Pause and Next episode in the taskbar preview (<see cref="ITaskbarToolbar"/>), from a
/// title's load until the player is left, minimized too. A press is the player's own button, so in a Watch Party
/// Play/Pause asks the room. When each works is the rule of Windows' media controls (<see cref="PlayerSystemMedia"/>).
/// </summary>
public sealed class PlayerTaskbarToolbar : IDisposable
{
    // What the buttons show is read from these; position ticks and the player's other changes leave them alone.
    private static readonly HashSet<string> ShownProperties =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.IsBuffering), nameof(PlayerViewModel.HasLeft),
        nameof(PlayerViewModel.MediaInfo), nameof(PlayerViewModel.HasPreviousEpisode), nameof(PlayerViewModel.HasNextEpisode),
        nameof(PlayerViewModel.IsWatchParty), nameof(PlayerViewModel.CanPauseInRoom),
    ];

    private readonly PlayerViewModel _player;
    private readonly ITaskbarToolbar _toolbar;
    private readonly IUiDispatcher _dispatcher;
    private TaskbarToolbarState? _shown;
    // Whether the title is a series episode, as far as known; kept while a new title loads.
    private bool _episode;
    private bool _disposed;

    public PlayerTaskbarToolbar(PlayerViewModel player, ITaskbarToolbar toolbar, IUiDispatcher dispatcher)
    {
        _player = player;
        _toolbar = toolbar;
        _dispatcher = dispatcher;
        _player.PropertyChanged += OnPlayerChanged;
        _toolbar.Pressed += OnPressed;
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
        _toolbar.Pressed -= OnPressed;
        Hide();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || ShownProperties.Contains(e.PropertyName))
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
        var state = Read();
        if (state == _shown)
        {
            return;
        }
        _shown = state;
        _toolbar.Show(state);
    }

    private TaskbarToolbarState Read()
    {
        var player = _player;
        _episode = IsEpisode();
        return new TaskbarToolbarState(
            EpisodeButton(_episode, PlayerSystemMedia.PreviousWorks(player)),
            PlayerSystemMedia.PlayPauseWorks(player) ? TaskbarButtonState.Enabled : TaskbarButtonState.Disabled,
            EpisodeButton(_episode, PlayerSystemMedia.NextWorks(player)),
            player.IsPaused);
    }

    // A series episode has Previous and Next. Until a new title's details have come its kind is not known: while it loads,
    // the buttons stay as they were, so Next does not take them away for the next episode's load; a title that failed
    // before its details came has none.
    private bool IsEpisode() => _player.MediaInfo == PlaybackMediaInfo.None
        ? _episode && _player.IsPreparing
        : _player.MediaInfo.Kind == PlaybackMediaKind.Episode;

    private static TaskbarButtonState EpisodeButton(bool shown, bool works) =>
        !shown ? TaskbarButtonState.Hidden : works ? TaskbarButtonState.Enabled : TaskbarButtonState.Disabled;

    private void Hide()
    {
        // A title started after is not known to be an episode.
        _episode = false;
        if (_shown is not null)
        {
            _shown = null;
            _toolbar.Hide();
        }
    }

    // Raised inside the window's message: handled once that has returned, so the taskbar is not called back from within it.
    private void OnPressed(object? sender, TaskbarButton button) => _dispatcher.Post(() => Press(button));

    // As the player's own buttons, on the state as it is now: a button may have gone grey since the press, a room may have
    // taken a guest's pause away.
    private void Press(TaskbarButton button)
    {
        if (_disposed)
        {
            return;
        }
        var player = _player;
        switch (button)
        {
            case TaskbarButton.Previous when PlayerSystemMedia.PreviousWorks(player):
                player.PlayPreviousEpisodeCommand.Execute(null);
                break;
            case TaskbarButton.PlayPause when PlayerSystemMedia.PlayPauseWorks(player):
                player.TogglePauseCommand.Execute(null);
                break;
            case TaskbarButton.Next when PlayerSystemMedia.NextWorks(player):
                player.PlayNextEpisodeCommand.Execute(null);
                break;
        }
    }
}
