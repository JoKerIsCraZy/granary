using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    public List<MediaSource> Loaded { get; } = [];

    public List<TimeSpan> Seeks { get; } = [];

    public PlaybackSnapshot Current { get; set; } = PlaybackSnapshot.Idle;

    public bool Disposed { get; private set; }

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public event EventHandler<PlaybackEnded>? Ended;

    public bool HasSubscribers => StateChanged is not null || Ended is not null;

    /// <summary>Thrown by the next <see cref="Load"/>, as mpv's engine refuses headers it cannot pass on.</summary>
    public Exception? FailNextLoad { get; set; }

    /// <summary>The id of the latest load; 0 before the first.</summary>
    public long LoadId { get; private set; }

    // The newest snapshot number handed out, as the engine numbers its snapshots.
    private long _sequence;

    public long Load(MediaSource source)
    {
        if (FailNextLoad is { } failure)
        {
            FailNextLoad = null;
            throw failure;
        }
        Loaded.Add(source);
        Current = Current with
        {
            LoadId = ++LoadId, IsLoaded = false, HasStarted = false, IsAtEnd = false, IsAudioOnly = false, Buffered = BufferedRanges.None,
            Sequence = ++_sequence,
        };
        return LoadId;
    }

    /// <summary>Every pause or play asked for, in order (true pauses).</summary>
    public List<bool> PauseRequests { get; } = [];

    public void SetPaused(bool paused)
    {
        PauseRequests.Add(paused);
        _pauseAsked = paused;
        Current = Current with { IsPaused = paused };
    }

    /// <summary>The latest pause asked; a state a test raises (mpv's own pause at the end, say) leaves it.</summary>
    public bool IsPauseAsked
    {
        get
        {
            bool asked = _pauseAsked;
            // Read now, set later: an ask in between finds the value read already old.
            Interleave();
            return asked;
        }
    }

    /// <summary>
    /// Runs once as the pause is next set from the asks, between reading them and setting the pause: an ask of the viewer
    /// or a room that arrives in that moment.
    /// </summary>
    public Action? WhilePauseIsApplied { get; set; }

    /// <summary>How often the latest ask was applied (<see cref="ApplyAskedPause"/>).</summary>
    public int AppliedAsks { get; private set; }

    private bool _pauseAsked;

    public void ApplyAskedPause()
    {
        // The engine applies the asks in one step: an ask in that moment comes first or after, never in between.
        Interleave();
        AppliedAsks++;
        Current = Current with { IsPaused = _pauseAsked };
    }

    private void Interleave()
    {
        var interleaved = WhilePauseIsApplied;
        WhilePauseIsApplied = null;
        interleaved?.Invoke();
    }

    public void Seek(TimeSpan position) => Seeks.Add(position);

    /// <summary>
    /// The volume and mute change only once a state says so (<see cref="RaiseState"/>), as mpv reports them a moment after
    /// they were set; until then <see cref="Current"/> has the ones before.
    /// </summary>
    public bool ReportsVolumeLater { get; set; }

    /// <summary>Every volume asked for, in order.</summary>
    public List<double> VolumeRequests { get; } = [];

    /// <summary>Every mute or unmute asked for, in order (true mutes).</summary>
    public List<bool> MuteRequests { get; } = [];

    public void SetVolume(double percent)
    {
        VolumeRequests.Add(percent);
        if (!ReportsVolumeLater)
        {
            Current = Current with { Volume = Math.Clamp(percent, 0, 100) };
        }
    }

    public void SetMuted(bool muted)
    {
        MuteRequests.Add(muted);
        if (!ReportsVolumeLater)
        {
            Current = Current with { IsMuted = muted };
        }
    }

    public double Speed { get; private set; } = 1;

    public void SetSpeed(double speed) => Speed = speed;

    public bool Fill { get; private set; }

    /// <summary>Thrown by <see cref="SetFill"/> while set, as an engine that has gone away might.</summary>
    public Exception? FailFill { get; set; }

    public void SetFill(bool fill)
    {
        if (FailFill is { } failure)
        {
            throw failure;
        }
        Fill = fill;
    }

    /// <summary>The audio delay in seconds, positive for later sound.</summary>
    public double AudioDelay { get; private set; }

    public void SetAudioDelay(double seconds) => AudioDelay = seconds;

    public PlaybackStats Stats { get; set; } = PlaybackStats.None;

    public PlaybackStats ReadStats() => Stats;

    /// <summary>Every audio switch asked for, in order.</summary>
    public List<int> AudioSwitches { get; } = [];

    /// <summary>What <see cref="SetAudioStream"/> answers: mpv refuses a stream the file lacks.</summary>
    public bool AudioSwitchTakes { get; set; } = true;

    public bool SetAudioStream(int stream)
    {
        AudioSwitches.Add(stream);
        return AudioSwitchTakes;
    }

    /// <summary>Every subtitle file read in, with the load it was for.</summary>
    public List<(Uri Url, long LoadId)> SubtitleFiles { get; } = [];

    /// <summary>What <see cref="AddSubtitleAsync"/> answers; a pending task keeps the file on its way.</summary>
    public Func<Uri, Task<int?>> ReadSubtitle { get; set; } = _ => Task.FromResult<int?>(3);

    /// <summary>The file's subtitle streams by FFmpeg index, as mpv numbers them.</summary>
    public Dictionary<int, int> EmbeddedSubtitles { get; } = [];

    /// <summary>Every subtitle stream shown, in order; 0 is none.</summary>
    public List<int> SubtitleStreams { get; } = [];

    public double SubtitleDelay { get; private set; }

    public Task<int?> AddSubtitleAsync(Uri url, long loadId)
    {
        SubtitleFiles.Add((url, loadId));
        return ReadSubtitle(url);
    }

    /// <summary>Every subtitle stream taken out again, in order.</summary>
    public List<int> RemovedSubtitles { get; } = [];

    public void RemoveSubtitle(int stream) => RemovedSubtitles.Add(stream);

    public int? SubtitleStreamOf(int ffmpegIndex) => EmbeddedSubtitles.TryGetValue(ffmpegIndex, out int stream) ? stream : null;

    public bool SetSubtitleStream(int stream)
    {
        SubtitleStreams.Add(stream);
        return true;
    }

    public void SetSubtitleDelay(double seconds) => SubtitleDelay = seconds;

    public SubtitleAppearance? SubtitleLook { get; private set; }

    public double SubtitleLift { get; private set; }

    public void SetSubtitleLook(SubtitleAppearance look) => SubtitleLook = look;

    public void SetSubtitleLift(double fraction) => SubtitleLift = fraction;

    public int Stops { get; private set; }

    public void Stop() => Stops++;

    /// <summary>
    /// Raises <paramref name="state"/>; without a load id of its own it belongs to the latest load, and without a number
    /// of its own it is the newest snapshot. One numbered below the newest arrives late, as the engine's threads can
    /// deliver one: it is raised, but what the engine holds stays the newest.
    /// </summary>
    public void RaiseState(PlaybackSnapshot state)
    {
        var raised = state.LoadId == 0 ? state with { LoadId = LoadId } : state;
        raised = raised.Sequence == 0 ? raised with { Sequence = ++_sequence } : raised;
        if (raised.Sequence >= Current.Sequence)
        {
            Current = raised;
            _sequence = Math.Max(_sequence, raised.Sequence);
        }
        StateChanged?.Invoke(this, raised);
    }

    /// <summary>Raises <paramref name="ended"/>; without a load id of its own it ends the latest load.</summary>
    public void RaiseEnded(PlaybackEnded ended) => Ended?.Invoke(this, ended.LoadId == 0 ? ended with { LoadId = LoadId } : ended);

    public void Dispose() => Disposed = true;
}

/// <summary>Runs posted work immediately, as if the test were the UI thread.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public void Post(Action action) => action();
}
