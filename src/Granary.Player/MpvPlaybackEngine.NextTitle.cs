using System.Security.Cryptography;
using System.Text;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// A changed mpv.conf applies from the next title (docs/architecture.md, "Your own mpv.conf"). mpv reads the file only when
// it starts, so a load compares what mpv would read now with what it last tried, off the caller's thread, and starts a new
// mpv only when they differ, by the path of any start: all of Granary's checks run again. Nothing restarts before a load,
// so a title that plays is never cut.
public sealed partial class MpvPlaybackEngine
{
    // Only the files mpv reads count here, no option in them.
    private static readonly HashSet<string> NoOptions = [];

    // The titles to open, one after the other, off the thread that loads them. Replaced under _gate.
    private Task _opens = Task.CompletedTask;

    // Under _gate, from a load until mpv is told to open its file: what mpv reports is of the file before, whose position,
    // end or refusal are not the new title's.
    private bool _fileBefore;

    // Under _gate, from a load until mpv was told: a pause waits for the title. mpv keeps a pause across files, and a file at
    // its end pauses itself again, so one sent earlier would be lost; and it is not the file before's to follow.
    private bool _pauseWaits;
    private bool? _pauseOnOpen;

    // What mpv would read, as it was when mpv last started with it, failed to, or refused it. Under _restartGate. It is not
    // tried again until it changes: later titles play as they are, and none of them pays for another start.
    private string _triedConfig = "";

    /// <summary>For tests: runs once a new mpv started, before the one before goes; one that throws stands for a start that failed.</summary>
    internal Action? AfterNewMpvStarted { get; set; }

    private bool FileBeforePlays
    {
        get
        {
            lock (_gate)
            {
                return _fileBefore;
            }
        }
    }

    // Under _gate: no title waits to open any more.
    private void ForgetOpening()
    {
        _activeLoad = 0;
        _fileBefore = false;
        _pauseWaits = false;
        _pauseOnOpen = null;
    }

    // In turn, off the thread that loaded it: mpv gets the title, after a new mpv if what it reads changed. A title a newer
    // load or a stop overtook does not open.
    private void Open(MediaSource source, long loadId)
    {
        try
        {
            OpenInTurn(source, loadId);
        }
        catch (Exception ex)
        {
            // The load has returned, and nobody waits for this task: its failure is said here, or nowhere.
            _logger.LogError(ex, "A title could not be opened");
            _mpv.Command("stop");
            EndWithoutPlaying(loadId, "Granary could not open the title");
        }
    }

    private void OpenInTurn(MediaSource source, long loadId)
    {
        if (!IsActiveLoad(loadId))
        {
            return;
        }
        _status.ReportTitleOpening();
        // Before the gate: a slow disk holds up this title and nothing else.
        string config = ReadConfigFingerprint();
        bool replaced = false;
        bool refused = false;
        lock (_restartGate)
        {
            if (_disposed || !IsActiveLoad(loadId))
            {
                return;
            }
            // After a stop Granary made (FailClosed) whose mpv without the file does not run yet, it starts here, and the
            // title plays in it. Should it not start, the title ends as a failure rather than play with the rejected
            // file, and the next load tries again.
            if (Volatile.Read(ref _failingClosed) != 0 && !StartWithoutConfig(Volatile.Read(ref _failClosedStops)))
            {
                refused = true;
            }
            else
            {
                replaced = ApplyChangedConfig(config);
                OpenFile(source, loadId);
            }
        }
        if (refused)
        {
            _logger.LogWarning("mpv does not run without mpv.conf; this title is not played");
            EndWithoutPlaying(loadId, "Granary could not start the player without your mpv.conf");
            return;
        }
        if (replaced)
        {
            RefreshSwapChain();
        }
        // A stop as the title opened, or as a new mpv started, may have reached mpv before the file did.
        if (IsActiveLoad(0))
        {
            _mpv.Command("stop");
        }
        _status.ReportTitleOpened();
    }

    private bool IsActiveLoad(long loadId)
    {
        lock (_gate)
        {
            return _activeLoad == loadId;
        }
    }

    // Under _restartGate: mpv is told to open the file, and then hears the pause asked for since the load.
    private void OpenFile(MediaSource source, long loadId)
    {
        string headers = string.Join(",", source.Headers.Select(h => $"{h.Key}: {h.Value}"));
        _mpv.SetProperty("http-header-fields", headers);

        // Plan URLs are replayed byte for byte (docs/api-contract.md, 9.2): the string as given, not as Uri re-escapes it.
        string target = source.Url.IsFile ? source.Url.LocalPath : source.Url.OriginalString;
        // With a mpv.conf, Granary's own options too, over the file's protocol and extension profiles.
        string perFileOptions = string.Join(",", PerFileOptions(source).Concat(_perFile));
        lock (_gate)
        {
            if (_activeLoad != loadId)
            {
                return;
            }
            // Right before the file reaches mpv: it can fail at once, and that end is this title's.
            _fileBefore = false;
        }
        _mpv.Command("loadfile", target, "replace", "-1", perFileOptions);
        bool? pause;
        lock (_gate)
        {
            // A newer load keeps its own wait.
            if (_activeLoad != loadId && _activeLoad != 0)
            {
                return;
            }
            _pauseWaits = false;
            pause = _pauseOnOpen;
            _pauseOnOpen = null;
        }
        if (pause is { } paused)
        {
            _mpv.SetProperty("pause", paused ? "yes" : "no");
        }
    }

    // On the open's thread: the title goes into no mpv and ends as a failure, unless a newer load or a stop came.
    private void EndWithoutPlaying(long loadId, string detail)
    {
        lock (_gate)
        {
            if (_activeLoad != loadId)
            {
                return;
            }
            ForgetOpening();
        }
        Update(s => PlaybackSnapshot.Idle with { IsPaused = s.IsPaused, Volume = s.Volume, IsMuted = s.IsMuted, LoadId = s.LoadId }, force: true);
        Ended?.Invoke(this, new PlaybackEnded(PlaybackEndReason.Error, detail, loadId));
    }

    // Under _restartGate: a new mpv when what mpv would read differs from what it last tried. True when one runs now. Should
    // it not start, the mpv before plays the title, and the same files are not tried again.
    private bool ApplyChangedConfig(string config)
    {
        if (config == _triedConfig)
        {
            return false;
        }
        _triedConfig = config;
        // mpv keeps a pause across files; so does a new one.
        string? pause = _mpv.GetString("pause");
        bool started = ReplaceMpv(() =>
        {
            // A new run for Settings: the file's state is what this start makes of it, a stop before it included.
            lock (_gate)
            {
                _stoppedThisRun = false;
            }
            Report(MpvConfigState.None);
            Start();
        }, "with mpv.conf");
        if (!started)
        {
            lock (_gate)
            {
                _status.Report(new MpvConfigState(MpvConfigUse.NotApplied));
            }
            return false;
        }
        if (pause is not null)
        {
            _mpv.SetProperty("pause", pause);
        }
        _logger.LogInformation("mpv.conf changed since mpv read it; a new mpv plays from this title");
        return true;
    }

    // What mpv would read now, by content: mpv.conf and the includes the pre-scan follows, each file's path and text in the
    // order they are read. A file that changes, appears or goes gives another value; shaders are not in it.
    private string ReadConfigFingerprint()
    {
        if (_options.ConfigDirectory is not { } directory)
        {
            return "";
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string part)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(part));
            hash.AppendData(new byte[1]);
        }
        string file = Path.Combine(directory, MpvConfigFolder.ConfigFileName);
        try
        {
            if (Directory.Exists(file))
            {
                Add("a folder");
            }
            else
            {
                MpvConfigText.Scan(file, directory, NoOptions, path =>
                {
                    Add(path);
                    try
                    {
                        string? text = _readConfig(path);
                        Add(text is null ? "missing" : "text " + text);
                        return text;
                    }
                    catch
                    {
                        Add("cannot be read");
                        throw;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // A start does not use what it cannot check (StartWith): as a state of the files, it is compared like any other.
            _logger.LogDebug(ex, "mpv's configuration could not be read to compare it");
            Add("cannot be checked");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Under _restartGate, for a change and for FailClosed alike: a new mpv made by start, then the one before closed, so the
    // graphics device and its driver stay in use all along. Should the new one not start, or anything after it fail, the
    // one before stays as it was (its events, Granary's options for it, the state Settings shows) with the engine's latest
    // settings, and whatever the new start made is closed. True when the new one runs.
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
            // What changed meanwhile went to the new mpv.
            RestoreGlobals();
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

    // The engine follows this mpv's events again: a start that failed gives it back.
    private void Attach(MpvClient client)
    {
        client.PropertyChanged += OnPropertyChanged;
        client.LogMessage += OnLogMessage;
        client.FileEnded += OnFileEnded;
        client.LifecycleEvent += OnLifecycleEvent;
    }
}
