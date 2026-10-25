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
