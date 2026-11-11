using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Granary.App.Services;

// Where the window opens: where it was closed (window.json), and in full screen when it was closed so. Saved once the window
// settles, so a crash, a kill or Windows shutting down keeps it too, and once more as it closes.
public sealed partial class WindowModeService
{
    // How long the window's place stays as it is before it is saved: a drag or a resize saves once, at its end.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private DispatcherQueueTimer? _settle;
    private bool _maximizeAtShow;
    private bool _fullScreenAtShow;
    // Closed in full screen while maximized: the launcher's fallbacks below go full screen again later, maximized under it.
    private bool _closedMaximizedInFullScreen;
    // A launcher showed the window minimized in place of full screen: full screen once it comes back from the taskbar.
    private bool _fullScreenOnRestore;
    // The place was saved as the window closed: changes the closing window still reports, such as the presenter it leaves
    // full screen for, are not its place, and no settle save follows.
    private bool _isClosed;

    /// <summary>
    /// Puts the window, before it shows, where it was closed last: false the first time, or when that could not be read,
    /// and Windows places it. One closed maximized is maximized as it shows, one closed in full screen goes full screen
    /// (<see cref="Show"/>); either way that place is the window it goes back to.
    /// </summary>
    public bool OpenAtLastPlace()
    {
        if (_window?.AppWindow is not { } window || placeFile.Load() is not { } place)
        {
            return false;
        }
        try
        {
            _windowed.Open(window, place);
            _maximizeAtShow = place.IsMaximized;
            _fullScreenAtShow = place.IsFullScreen;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window could not open where it was closed; Windows places it");
            return false;
        }
    }

    /// <summary>
    /// Shows the window the first time, and activates it. One closed in full screen goes full screen right before the
    /// activation, so its first frame is full screen already. One closed maximized shows maximized at once: maximizing shows
    /// a window, so it is done here, right before the activation, and not while the app is still being made; in full
    /// screen it is maximized as it leaves full screen instead.
    /// </summary>
    public void Show()
    {
        var window = _window ?? throw new InvalidOperationException("MainWindow has not attached itself.");
        bool maximize = _maximizeAtShow;
        bool fullScreen = _fullScreenAtShow;
        _maximizeAtShow = false;
        _fullScreenAtShow = false;
        if (fullScreen)
        {
            _closedMaximizedInFullScreen = maximize;
            GoFullScreenAsClosed();
        }
        if (maximize && !IsFullScreen)
        {
            Maximize(window.AppWindow);
        }
        window.Activate();
        if (fullScreen)
        {
            FollowFirstFullScreenShow(window.AppWindow);
        }
        else if (maximize)
        {
            FollowFirstShow(window.AppWindow);
        }
    }

    // Full screen, as Granary closed in it. One closed maximized too is not maximized first, which would show it as a window,
    // but on its way out of full screen.
    private void GoFullScreenAsClosed()
    {
        SetFullScreen(true);
        if (_closedMaximizedInFullScreen && IsFullScreen)
        {
            _windowed.MaximizeOnRestore();
        }
    }

    // A launcher's own show command can take the first show here too. Should the window have given up full screen for it, it
    // goes full screen now, or, shown minimized, once it comes back from the taskbar; one that is still full screen comes
    // back so by itself.
    private void FollowFirstFullScreenShow(AppWindow window)
    {
        try
        {
            var shown = ShownMode(window);
            if (shown == WindowMode.FullScreen)
            {
                return;
            }
            if (shown != _modes.Mode)
            {
                FollowShown(shown);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            if (WindowedPlacement.IsMinimized(window))
            {
                logger.LogDebug("The window showed minimized at first; it goes full screen as it comes back");
                _fullScreenOnRestore = true;
                return;
            }
            logger.LogDebug("The window showed as a window at first; it goes full screen now");
            GoFullScreenAsClosed();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window did not open in full screen as it was closed");
        }
    }

    // The window shown minimized at first (FollowFirstFullScreenShow) is back: full screen, once this change is through, if
    // it is still the window it came back as, a window and not minimized again. Any change the app asks for meanwhile ends
    // the wait (Change), and so does this, whatever it finds.
    private void FollowRestoreToFullScreen(AppWindow window)
    {
        if (!_fullScreenOnRestore || !IsBackAsAWindow(window))
        {
            return;
        }
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_fullScreenOnRestore)
            {
                return;
            }
            _fullScreenOnRestore = false;
            if (_modes.Mode == WindowMode.Window && _window?.AppWindow is { } current && IsBackAsAWindow(current))
            {
                GoFullScreenAsClosed();
            }
        });
    }

    // Shown as a window, neither minimized nor full screen or the mini view.
    private static bool IsBackAsAWindow(AppWindow window) =>
        window.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized } && !WindowedPlacement.IsMinimized(window);

    // Whether the next start opens in full screen, were the window to close now; a window that no longer says counts as
    // not minimized.
    private bool ReopensInFullScreen()
    {
        try
        {
            return _modes.ReopensInFullScreen(_window?.AppWindow is { } window && WindowedPlacement.IsMinimized(window));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The closing window did not say whether it is minimized");
            return _modes.ReopensInFullScreen(isMinimized: false);
        }
    }

    // A launcher's own show command (a shortcut's "Run") can take the place of a process's first one: shown restored, the
    // window is maximized now; shown minimized, as the shortcut asks, it comes back maximized.
    private void FollowFirstShow(AppWindow window)
    {
        try
        {
            switch (window.Presenter)
            {
                case OverlappedPresenter { State: OverlappedPresenterState.Restored }:
                    logger.LogDebug("The window showed restored at first; it is maximized now");
                    _windowed.Maximize(window);
                    break;
                case OverlappedPresenter { State: OverlappedPresenterState.Minimized }:
                    logger.LogDebug("The window showed minimized at first; it comes back maximized");
                    _windowed.RestoreToMaximized(window);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window was not maximized as it was closed");
        }
    }

    private void AttachPlace(Window window)
    {
        _settle = window.DispatcherQueue.CreateTimer();
        _settle.Interval = SettleDelay;
        _settle.IsRepeating = false;
        WeakTick.Subscribe(_settle, this, static modes => modes.SaveSettledPlace());
        // However it closes: the window's Close, full screen's Close, Windows' own, or a restart into an update.
        window.Closed += (_, _) => SaveClosingPlace();
    }

    // The window as a window changed: kept, and saved once it stays so.
    private void KeepPlace(AppWindow window)
    {
        if (_isClosed)
        {
            return;
        }
        _windowed.Keep(window);
        SettleSoon();
    }

    // Saved once the window stays as it is for a moment: its place, and whether it is full screen.
    private void SettleSoon()
    {
        if (_isClosed)
        {
            return;
        }
        _settle?.Stop();
        _settle?.Start();
    }

    private void Maximize(AppWindow window)
    {
        try
        {
            _windowed.Maximize(window);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window was not maximized as it was closed");
        }
    }

    // Off the UI thread; a place the file holds already is not written again.
    private void SaveSettledPlace()
    {
        if (_isClosed)
        {
            return;
        }
        try
        {
            if (_windowed.PlaceFor(ReopensInFullScreen()) is { } place)
            {
                _ = placeFile.SaveAsync(place);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window's place was not saved");
        }
    }

    // The window as it would come back from full screen or the mini view, and whether it was full screen: so closing in full
    // screen opens in it next time, with that window to go back to; closing in the mini view opens as that window, and a
    // minimized one where it was before. Written here, after a save in flight.
    private void SaveClosingPlace()
    {
        _isClosed = true;
        _settle?.Stop();
        try
        {
            if (_modes.Mode == WindowMode.Window && _window?.AppWindow is { } window)
            {
                _windowed.Keep(window);
            }
        }
        catch (Exception ex)
        {
            // A window that is closing may not say any more; it was kept as it changed.
            logger.LogDebug(ex, "The closing window was not kept once more");
        }
        try
        {
            if (_windowed.PlaceFor(ReopensInFullScreen()) is { } place)
            {
                placeFile.Save(place);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window's place was not saved");
        }
    }
}
