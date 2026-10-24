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

    public void Load(MediaSource source) => Loaded.Add(source);

    public void SetPaused(bool paused) => Current = Current with { IsPaused = paused };

    public void Seek(TimeSpan position) => Seeks.Add(position);

    public void Stop()
    {
    }

    public void RaiseState(PlaybackSnapshot state)
    {
        Current = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaiseEnded(PlaybackEnded ended) => Ended?.Invoke(this, ended);

    public void Dispose() => Disposed = true;
}

/// <summary>Runs posted work immediately, as if the test were the UI thread.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public void Post(Action action) => action();
}
