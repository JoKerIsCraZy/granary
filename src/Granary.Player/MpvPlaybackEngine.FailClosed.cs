using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// When a profile of the viewer's mpv.conf keeps changing one of Granary's options, or mpv does not take Granary's value
// back, the protection fails closed (docs/architecture.md, "Your own mpv.conf"): the file that plays is stopped at once,
// so nothing more is read or written under the file's value, and mpv starts again without the file, as when the file is
// refused at the start. The load that played ends as a failure: a server playback reconnects by itself, and the player
// of a local file offers to try again. The engine's own settings (volume, speed, subtitles, fill) carry over.
public sealed partial class MpvPlaybackEngine
{
    // Guards replacing the mpv instance against a load, the engine's start and its disposal.
    private readonly object _restartGate = new();

    // Guards what the engine set, and its way into mpv, against a new mpv taking it: a change while mpv starts again reaches
    // that mpv, whichever comes first, and the latest value is the one that stays.
    private readonly object _globalsGate = new();

    // What the engine set that mpv keeps from one file to the next, by option, to set again in a new mpv. Under _globalsGate.
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    // The load mpv plays or opens, 0 when none; under _gate.
    private long _activeLoad;

    // 1 from the moment Granary stops the file until mpv runs without the mpv.conf. It stays while that start fails, so no
    // title goes into the mpv that still holds the file (Open), and the next load tries again.
    private int _failingClosed;

    // Each stop Granary made, and the last one an mpv without the file started for (under _restartGate). A load that comes
    // before the start after a stop makes that start itself, so its title plays in the new mpv, not in the one that goes.
    private int _failClosedStops;
    private int _startedWithoutConfigFor;

    /// <summary>For tests: runs on the start after a stop (FailClosed) before it takes the restart gate, as a slow thread pool would.</summary>
    internal Action? BeforeStartWithoutConfig { get; set; }

    // Under _restartGate.
    private bool _disposed;

    // A setting mpv keeps across files: remembered, so a new mpv gets it too.
    private void SetGlobal(string name, string value)
    {
        lock (_globalsGate)
        {
            _globals[name] = value;
            _mpv.SetProperty(name, value);
        }
    }

    // On mpv's event thread: stops the file now; mpv starts again without the mpv.conf on another thread, as this one
    // ends with the mpv it belongs to.
    private void FailClosed(string reason)
    {
        if (Interlocked.Exchange(ref _failingClosed, 1) != 0)
        {
            return;
        }
        int stop = Interlocked.Increment(ref _failClosedStops);
        _logger.LogWarning("mpv.conf {Reason}; Granary stopped and plays without your mpv.conf", reason);
        Report(new MpvConfigState(MpvConfigUse.StoppedDuringPlayback, "it " + reason));
        // A title that still waits for its turn has nothing to do with the file that plays: it opens in the mpv without the
        // file (Open).
        long load = 0;
        lock (_gate)
        {
            if (!_fileBefore)
            {
                load = _activeLoad;
                ForgetOpening();
            }
        }
        _mpv.Command("stop");
        _ = Task.Run(() => RestartWithoutConfig(load, stop));
    }

    private void RestartWithoutConfig(long load, int stop)
    {
        try
        {
            BeforeStartWithoutConfig?.Invoke();
            lock (_restartGate)
            {
                if (_disposed)
                {
                    return;
                }
                StartWithoutConfig(stop);
            }
        }
        catch (Exception ex)
        {
            // ReplaceMpv keeps the mpv before when the new one fails; this only keeps a thread-pool task from throwing.
            _logger.LogError(ex, "mpv did not start again without mpv.conf");
        }
        RefreshSwapChain();
        bool loadedSince;
        lock (_gate)
        {
            loadedSince = _activeLoad != 0;
        }
        if (!loadedSince)
        {
            Update(s => PlaybackSnapshot.Idle with { IsPaused = s.IsPaused, Volume = s.Volume, IsMuted = s.IsMuted, LoadId = s.LoadId }, force: true);
        }
        if (load != 0)
        {
            Ended?.Invoke(this, new PlaybackEnded(PlaybackEndReason.Error, "Granary stopped using your mpv.conf", load));
        }
    }

    // Under _restartGate: mpv without the file for this stop, whether the task after the stop or a load that came first
    // makes it; true once it runs, and only then is the stop over. mpv without the file is how it always started; should
    // even that fail, the mpv before stays (ReplaceMpv), holding the file, and takes no title until a later load's try works.
    private bool StartWithoutConfig(int stop)
    {
        if (_startedWithoutConfigFor >= stop)
        {
            return true;
        }
        bool started = ReplaceMpv(() =>
        {
            Prepare(null);
            Launch(null);
        }, "without mpv.conf");
        if (started)
        {
            _startedWithoutConfigFor = stop;
            Volatile.Write(ref _failingClosed, 0);
        }
        return started;
    }

    // Under _restartGate, into the mpv that runs now: the engine's settings, and the size of the picture Granary shows now.
    private void RestoreGlobals()
    {
        lock (_globalsGate)
        {
            foreach (var (name, value) in _globals)
            {
                _mpv.SetProperty(name, value);
            }
            if (_options.VideoOutput == VideoOutputKind.D3D11Composition)
            {
                _mpv.SetProperty(MpvOwnOptions.CompositionSize, SizeText(_outputSize));
            }
        }
    }

    // The text of a config file Granary checks before mpv reads it, or null when there is none (mpv passes it over).
    private static string? ReadConfigText(string path)
    {
        try
        {
            return MpvConfigFolder.ReadForCheck(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
