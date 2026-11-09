using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Granary.App.Services;

// Where the window opens: where it was closed (window.json). Saved once the window settles, so a crash, a kill or Windows
// shutting down keeps it too, and once more as it closes.
public sealed partial class WindowModeService
{
    // How long the window's place stays as it is before it is saved: a drag or a resize saves once, at its end.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private DispatcherQueueTimer? _settle;
    private bool _maximizeAtShow;
    // The place was saved as the window closed: changes the closing window still reports, such as the presenter it leaves
    // full screen for, are not its place, and no settle save follows.
    private bool _isClosed;

    /// <summary>
    /// Puts the window, before it shows, where it was closed last: false the first time, or when that could not be read,
    /// and Windows places it. One closed maximized is maximized as it shows (<see cref="Show"/>).
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
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window could not open where it was closed; Windows places it");
            return false;
        }
    }

    /// <summary>
    /// Shows the window the first time, and activates it. One closed maximized shows maximized at once: maximizing shows a
    /// window, so it is done here, right before the activation, and not while the app is still being made.
    /// </summary>
    public void Show()
    {
        var window = _window ?? throw new InvalidOperationException("MainWindow has not attached itself.");
        bool maximize = _maximizeAtShow;
        _maximizeAtShow = false;
        if (maximize)
        {
            Maximize(window.AppWindow);
        }
        window.Activate();
        if (maximize)
        {
            FollowFirstShow(window.AppWindow);
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
            if (_windowed.Place is { } place)
            {
                _ = placeFile.SaveAsync(place);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The window's place was not saved");
        }
    }

    // The window as it would come back from full screen or the mini view: so closing in either opens as the window next
    // time, and a minimized one where it was before. Written here, after a save in flight.
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
            if (_windowed.Place is { } place)
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
