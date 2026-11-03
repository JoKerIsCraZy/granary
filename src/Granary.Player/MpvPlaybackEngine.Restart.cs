using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// Apply now (docs/architecture.md, "Your own mpv.conf"): mpv starts again and reads mpv.conf as it is now, along the
// path FailClosed takes, with the file instead of without it. The new mpv runs before the one before goes, Granary's own
// options are kept as at any start, the engine's settings carry over, and the window gets the new swap chain. Playback is
// never cut: while a title plays, or while Granary stops one (FailClosed), the start waits for the next load, or for the
// file to end or be stopped (the player closing stops it). A start that fails keeps the mpv before.
public sealed partial class MpvPlaybackEngine
{
    // Apply now is waiting; written under _restartGate, read on mpv's event thread too.
    private volatile bool _restartPending;

    /// <summary>mpv runs with mpv.conf as it is now, after <see cref="RestartWithConfig"/>. Raised on any thread.</summary>
    internal event EventHandler? ConfigRestarted;

    /// <summary>
    /// Starts mpv again with mpv.conf as it is now: at once when nothing plays (true); otherwise at the next load, or once
    /// the file that plays ends or is stopped (false). <see cref="ConfigRestarted"/> follows once mpv runs again; a start
    /// that failed keeps the mpv before, and raises nothing.
    /// </summary>
    internal bool RestartWithConfig()
    {
        bool started;
        lock (_restartGate)
        {
            if (_disposed)
            {
                return true;
            }
            // Marked first, then checked: a file that ends meanwhile either sees the mark or is seen to have ended.
            _restartPending = true;
            if (!CanStartAgain)
            {
                _logger.LogInformation("mpv starts again with mpv.conf once the title that plays ends");
                return false;
            }
            started = StartAgain();
        }
        if (started)
        {
            AfterStartingAgain();
        }
        return true;
    }

    // Nothing plays or is held at its end (_activeLoad), and Granary is not stopping a title itself (FailClosed), which
    // starts its own mpv and says when it is done (StartAgainIfWaiting).
    private bool CanStartAgain
    {
        get
        {
            lock (_gate)
            {
                return _activeLoad == 0 && Volatile.Read(ref _failingClosed) == 0;
            }
        }
    }

    // Under _restartGate, for a load: a start that waited happens before the new title, which then plays with the file as
    // it is now. True when mpv started again.
    private bool StartAgainBeforeLoad() =>
        _restartPending && !_disposed && Volatile.Read(ref _failingClosed) == 0 && StartAgain();

    // Once a file ended or was stopped, on mpv's event thread, or once FailClosed's own start is done: a start that waited
    // happens on another thread, as the event thread ends with the mpv it belongs to.
    private void StartAgainIfWaiting()
    {
        if (_restartPending)
        {
            _ = Task.Run(StartAgainNowIdle);
        }
    }

    private void StartAgainNowIdle()
    {
        bool started;
        lock (_restartGate)
        {
            // A load came first and started mpv again itself, or a title plays now: the next load will.
            if (_disposed || !_restartPending || !CanStartAgain)
            {
                return;
            }
            started = StartAgain();
        }
        if (started)
        {
            AfterStartingAgain();
        }
    }

    // Under _restartGate: a new mpv with the file as it is now, then the one before closed. Should the new one not start
    // at all, the one before stays, as it was, and the start is still to come.
    private bool StartAgain()
    {
        var before = _mpv;
        var (own, kept, perFile) = (_own, _kept, _perFile);
        MpvConfigState status;
        bool stopped;
        lock (_gate)
        {
            status = _status.Current;
            stopped = _stoppedThisRun;
            // A new run for Settings: the file's state is what this start makes of it, a stop before it included.
            _stoppedThisRun = false;
        }
        Detach(before);
        try
        {
            Report(MpvConfigState.None);
            Start();
            RestoreGlobals();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mpv did not start again with mpv.conf; it plays on as before");
            // A new mpv that started before something after it failed goes too; one that failed itself is closed already.
            if (!ReferenceEquals(_mpv, before))
            {
                Close(_mpv);
            }
            _mpv = before;
            (_own, _kept, _perFile) = (own, kept, perFile);
            Attach(before);
            lock (_gate)
            {
                _stoppedThisRun = stopped;
                _status.Report(status);
            }
            return false;
        }
        _restartPending = false;
        before.Dispose();
        _logger.LogInformation("mpv started again to read mpv.conf as it is now");
        return true;
    }

    // Outside _restartGate: the window takes the new swap chain, and Settings hears of the new start.
    private void AfterStartingAgain()
    {
        if (_disposed)
        {
            return;
        }
        RefreshSwapChain();
        ConfigRestarted?.Invoke(this, EventArgs.Empty);
    }

    // The engine follows this mpv's events again: a start that failed gives it back.
    private void Attach(MpvClient client)
    {
        client.PropertyChanged += OnPropertyChanged;
        client.LogMessage += OnLogMessage;
        client.FileEnded += OnFileEnded;
        client.LifecycleEvent += OnLifecycleEvent;
    }
}
