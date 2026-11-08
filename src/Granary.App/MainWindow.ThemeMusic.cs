using Granary.Core.Playback.Themes;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;

namespace Granary.App;

// Title pages' theme music (Core's ThemeMusic) gives way to what the window plays: the player, over the window or as its bar,
// and a trailer. A minimized window counts as a page that left, as the apps take the app going to the background.
public sealed partial class MainWindow
{
    private ThemeMusic? _themeMusic;
    private bool _otherMediaPending;
    // A title replacing the one that played waits for the old player to let the picture go (MainWindow.Player.cs).
    private bool _playerOpening;

    private void InitializeThemeMusic()
    {
        _themeMusic = App.Services.GetRequiredService<ThemeMusic>();
        Trailers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrailerOverlayViewModel.State))
            {
                ShowOtherMedia();
            }
        };
        AppWindow.Changed += (sender, _) =>
            _themeMusic.IsMinimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        Closed += (_, _) => _themeMusic.Dispose();
    }

    // Read once the window's turn is done: a player that takes over from a trailer closes the trailer first, which would
    // otherwise let the theme start for a moment between the two.
    private void ShowOtherMedia()
    {
        if (_otherMediaPending)
        {
            return;
        }
        _otherMediaPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _otherMediaPending = false;
            _themeMusic?.OtherMediaPlaying = IsPlayerOpen || _playerOpening || Trailers.IsOpen;
        });
    }
}
