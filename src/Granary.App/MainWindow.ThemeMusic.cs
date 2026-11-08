using Granary.App.Controls;
using Granary.Core.Playback.Themes;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace Granary.App;

// Title pages' theme music (Core's ThemeMusic) gives way to what the window plays: the player, over the window or as its bar,
// and a trailer. A minimized window counts as a page that left, as the apps take the app going to the background.
public sealed partial class MainWindow
{
    // How long a title replacing the one that played counts as playing before it opens, should the old player never say
    // it let the picture go.
    private static readonly TimeSpan PlayerOpeningLimit = TimeSpan.FromSeconds(3);

    private ThemeMusic? _themeMusic;
    private bool _otherMediaPending;
    // A title replacing the one that played waits for the old player to let the picture go (MainWindow.Player.cs).
    private bool _playerOpening;
    private DispatcherQueueTimer? _playerOpeningLimit;

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
        _playerOpeningLimit = DispatcherQueue.CreateTimer();
        _playerOpeningLimit.Interval = PlayerOpeningLimit;
        _playerOpeningLimit.IsRepeating = false;
        WeakTick.Subscribe(_playerOpeningLimit, this, static window => window.EndPlayerOpening());
        Closed += (_, _) =>
        {
            _playerOpeningLimit.Stop();
            _themeMusic.Dispose();
        };
    }

    // The next title counts as playing from its Play until it opens, fails to, the player closes, or the limit passes.
    private void BeginPlayerOpening()
    {
        _playerOpening = true;
        _playerOpeningLimit?.Start();
        ShowOtherMedia();
    }

    private void EndPlayerOpening()
    {
        _playerOpeningLimit?.Stop();
        if (!_playerOpening)
        {
            return;
        }
        _playerOpening = false;
        ShowOtherMedia();
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
