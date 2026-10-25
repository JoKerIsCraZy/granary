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

    public long Load(MediaSource source)
    {
        if (FailNextLoad is { } failure)
        {
            FailNextLoad = null;
            throw failure;
        }
        Loaded.Add(source);
        Current = Current with { LoadId = ++LoadId, IsLoaded = false, HasStarted = false, IsAtEnd = false };
        return LoadId;
    }

    public void SetPaused(bool paused) => Current = Current with { IsPaused = paused };

    public void Seek(TimeSpan position) => Seeks.Add(position);

    public void SetVolume(double percent) => Current = Current with { Volume = Math.Clamp(percent, 0, 100) };

    public void SetMuted(bool muted) => Current = Current with { IsMuted = muted };

    public double Speed { get; private set; } = 1;

    public void SetSpeed(double speed) => Speed = speed;

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

    /// <summary>Raises <paramref name="state"/>; without a load id of its own it belongs to the latest load.</summary>
    public void RaiseState(PlaybackSnapshot state)
    {
        Current = state.LoadId == 0 ? state with { LoadId = LoadId } : state;
        StateChanged?.Invoke(this, Current);
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
