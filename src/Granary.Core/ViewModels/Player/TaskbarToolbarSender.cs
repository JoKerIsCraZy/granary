using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Hands the taskbar toolbar's state to Windows for its Windows side (<see cref="ITaskbarToolbar"/>), and keeps it handed
/// on. Explorer is another process that can be busy for a moment or restarting: a send that failed is sent again a moment
/// later, also when nothing changed meanwhile, and only failures spread over <see cref="GiveUpAfter"/> give up, hiding the
/// buttons as far as Windows still takes that, so none stays that no longer works. Given up, a change after a quiet minute
/// tries again, and so does a new taskbar button (<see cref="Restart"/>). All on the UI thread.
/// </summary>
public sealed class TaskbarToolbarSender : IDisposable
{
    /// <summary>How long after a failure the state is sent again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Failures in a row give up once they span this long.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(10);

    /// <summary>Given up, a change this long after the last failure tries again.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromMinutes(1);

    // And at least this many: one slow answer is no reason to give up.
    private const int FailuresToGiveUp = 3;

    private readonly Action<TaskbarToolbarState?> _send;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // What should show; null for nothing.
    private TaskbarToolbarState? _state;
    private ITimer? _retry;
    private int _failures;
    private long _firstFailure;
    private long _lastFailure;
    private bool _givenUp;
    private bool _disposed;

    /// <param name="send">Hands a state to Windows, null hiding every button; throws when Windows did not take it.</param>
    /// <param name="dispatcher">The UI thread's, where a send runs again.</param>
    /// <param name="time">The clock of the retries and of giving up.</param>
    /// <param name="logger">Where failures are logged.</param>
    public TaskbarToolbarSender(Action<TaskbarToolbarState?> send, IUiDispatcher dispatcher, TimeProvider time, ILogger logger)
    {
        _send = send;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>What shows from now on, null for nothing; sent at once, unless the sends have given up.</summary>
    public void Send(TaskbarToolbarState? state)
    {
        _state = state;
        if (!_givenUp || _time.GetElapsedTime(_lastFailure) >= QuietTime)
        {
            TrySend();
        }
    }

    /// <summary>Sends what shows again, as after new icons were made.</summary>
    public void Resend() => Send(_state);

    /// <summary>A new taskbar button, whose toolbar is empty and may belong to a new Explorer: failures before do not count.</summary>
    public void Restart()
    {
        ForgetFailures();
        TrySend();
    }

    public void Dispose()
    {
        _disposed = true;
        StopRetry();
    }

    private void TrySend()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _send(_state);
        }
        catch (Exception ex)
        {
            Failed(ex);
            return;
        }
        ForgetFailures();
    }

    private void Failed(Exception ex)
    {
        long now = _time.GetTimestamp();
        if (_failures++ == 0)
        {
            _firstFailure = now;
        }
        _lastFailure = now;
        _givenUp |= _failures >= FailuresToGiveUp && _time.GetElapsedTime(_firstFailure, now) >= GiveUpAfter;
        _logger.LogWarning("The taskbar preview's buttons failed: {Failure} {Result:X8}{GivenUp}", ex.GetType().Name, ex.HResult,
            _givenUp ? "; the player goes on without them" : "");
        if (_givenUp)
        {
            StopRetry();
            HideIfPossible();
        }
        else
        {
            _retry ??= _time.CreateTimer(static sender => ((TaskbarToolbarSender)sender!).OnRetryDue(), this, RetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // As far as Windows still takes it, no button stays that no longer works.
    private void HideIfPossible()
    {
        if (_state is null)
        {
            return;
        }
        try
        {
            _send(null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Hiding the taskbar preview's buttons failed too: {Failure} {Result:X8}", ex.GetType().Name, ex.HResult);
        }
    }

    // On the timer's thread: the send is the UI thread's.
    private void OnRetryDue() => _dispatcher.Post(Retry);

    private void Retry()
    {
        StopRetry();
        if (!_givenUp)
        {
            TrySend();
        }
    }

    private void ForgetFailures()
    {
        _failures = 0;
        _givenUp = false;
        StopRetry();
    }

    private void StopRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }
}
