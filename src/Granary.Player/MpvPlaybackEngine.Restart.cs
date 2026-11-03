using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// Apply now (docs/architecture.md, "Your own mpv.conf"): mpv starts again and reads mpv.conf as it is now, as FailClosed
// starts it again without the file; both replace the mpv through ReplaceMpv. The new mpv runs before the one before goes,
// Granary's own options are kept as at any start, the engine's settings carry over, and the window gets the new swap
// chain. Playback is never cut: while a title plays, or while Granary stops one (FailClosed), the start waits for the
// next load, or for the file to end or be stopped (the player closing stops it). A start that fails keeps the mpv before.
public sealed partial class MpvPlaybackEngine
{
    // Apply now is waiting; written under _restartGate, read on mpv's event thread too.
    private volatile bool _restartPending;

    /// <summary>mpv runs with mpv.conf as it is now, after <see cref="RestartWithConfig"/>. Raised on any thread.</summary>
    internal event EventHandler? ConfigRestarted;

    /// <summary>A start for <see cref="RestartWithConfig"/> failed, at once or once it had waited. Raised on any thread.</summary>
    internal event EventHandler? ConfigRestartFailed;

    /// <summary>For tests: runs once a new mpv started, before the one before goes; one that throws stands for a start that failed.</summary>
    internal Action? AfterNewMpvStarted { get; set; }

    /// <summary>
    /// Starts mpv again with mpv.conf as it is now: at once when nothing plays; otherwise at the next load, or once the
    /// file that plays ends or is stopped. <see cref="ConfigRestarted"/> or <see cref="ConfigRestartFailed"/> follows once
    /// it was tried; a start that failed keeps the mpv before, and is tried again with the next load.
    /// </summary>
    internal MpvRestartOutcome RestartWithConfig()
    {
        bool started;
        lock (_restartGate)
        {
            if (_disposed)
            {
                return MpvRestartOutcome.Started;
            }
            // Marked first, then checked: a file that ends meanwhile either sees the mark or is seen to have ended.
            _restartPending = true;
            if (!CanStartAgain)
            {
                _logger.LogInformation("mpv starts again with mpv.conf once the title that plays ends");
                return MpvRestartOutcome.Waiting;
            }
            started = StartAgain();
        }
        AfterStartingAgain(started);
        return started ? MpvRestartOutcome.Started : MpvRestartOutcome.Failed;
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
    // it is now. Null when none waited; else whether mpv started again.
    private bool? StartAgainBeforeLoad() =>
        _restartPending && !_disposed && Volatile.Read(ref _failingClosed) == 0 ? StartAgain() : null;

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
        AfterStartingAgain(started);
    }

    // Under _restartGate: a new mpv with the file as it is now. A new run for Settings: the file's state is what this start
    // makes of it, a stop before it included.
    private bool StartAgain()
    {
        bool started = ReplaceMpv(() =>
        {
            lock (_gate)
            {
                _stoppedThisRun = false;
            }
            Report(MpvConfigState.None);
            Start();
        }, "with mpv.conf");
        if (started)
        {
            _restartPending = false;
            _logger.LogInformation("mpv started again to read mpv.conf as it is now");
        }
        return started;
    }

    // Under _restartGate, for Apply now and FailClosed alike: a new mpv made by start, then the one before closed, so the
    // graphics device and its driver stay in use all along. Should the new one not start, or anything after it fail, the
    // one before stays as it was (its events, Granary's options for it, the state Settings shows), and whatever the new
    // start made is closed. True when the new one runs.
    private bool ReplaceMpv(Action start, string purpose)
    {
        var before = _mpv;
        var (own, kept, perFile) = (_own, _kept, _perFile);
        MpvConfigState status;
        bool stopped;
        lock (_gate)
        {
            status = _status.Current;
            stopped = _stoppedThisRun;
        }
        Detach(before);
        try
        {
            start();
            RestoreGlobals();
            AfterNewMpvStarted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A new mpv did not start {Purpose}; the one before plays on", purpose);
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
        before.Dispose();
        return true;
    }

    // Outside _restartGate: the window takes the new swap chain, and Settings hears how the start went.
    private void AfterStartingAgain(bool started)
    {
        if (_disposed)
        {
            return;
        }
        if (!started)
        {
            ConfigRestartFailed?.Invoke(this, EventArgs.Empty);
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
