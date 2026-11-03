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

    // What the engine set that mpv keeps from one file to the next, by option, to set again in a new mpv. Under _gate.
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    // The load mpv plays or opens, 0 when none; under _gate.
    private long _activeLoad;

    // 1 from the moment Granary stops the file until mpv runs again without the mpv.conf.
    private int _failingClosed;

    // Under _restartGate.
    private bool _disposed;

    // A setting mpv keeps across files: remembered, so a new mpv gets it too.
    private void SetGlobal(string name, string value)
    {
        lock (_gate)
        {
            _globals[name] = value;
        }
        _mpv.SetProperty(name, value);
    }

    // On mpv's event thread: stops the file now; mpv starts again without the mpv.conf on another thread, as this one
    // ends with the mpv it belongs to.
    private void FailClosed(string reason)
    {
        if (Interlocked.Exchange(ref _failingClosed, 1) != 0)
        {
            return;
        }
        _logger.LogWarning("mpv.conf {Reason}; Granary stopped and plays without your mpv.conf", reason);
        long load;
        lock (_gate)
        {
            load = _activeLoad;
            _activeLoad = 0;
        }
        _mpv.Command("stop");
        _ = Task.Run(() => RestartWithoutConfig(load));
    }

    private void RestartWithoutConfig(long load)
    {
        try
        {
            lock (_restartGate)
            {
                if (_disposed)
                {
                    return;
                }
                // The new mpv runs before the one before goes, so the graphics device and its driver stay in use all along.
                var before = _mpv;
                Detach(before);
                try
                {
                    Prepare(null);
                    Launch(null);
                    RestoreGlobals();
                }
                finally
                {
                    before.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // mpv without the file is how it always started; should even that fail, the player says so as for any failure.
            _logger.LogError(ex, "mpv did not start again without mpv.conf");
        }
        finally
        {
            Volatile.Write(ref _failingClosed, 0);
        }
        RefreshSwapChain();
        var idle = Update(s => PlaybackSnapshot.Idle with { IsPaused = s.IsPaused, Volume = s.Volume, IsMuted = s.IsMuted, LoadId = s.LoadId }, force: true);
        if (load != 0)
        {
            Ended?.Invoke(this, new PlaybackEnded(PlaybackEndReason.Error, "Granary stopped using your mpv.conf", load));
        }
    }

    // Under _restartGate, into the new mpv: the engine's settings, and the size of the picture Granary shows now.
    private void RestoreGlobals()
    {
        KeyValuePair<string, string>[] globals;
        lock (_gate)
        {
            globals = [.. _globals];
        }
        foreach (var (name, value) in globals)
        {
            _mpv.SetProperty(name, value);
        }
        if (_options.VideoOutput == VideoOutputKind.D3D11Composition)
        {
            _mpv.SetProperty(MpvOwnOptions.CompositionSize, SizeText(_outputSize));
        }
    }

    // The text of a config file Granary checks before mpv reads it, or null when there is none (mpv passes it over).
    private static string? ReadConfigText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
