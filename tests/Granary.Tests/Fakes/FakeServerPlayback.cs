using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>Records what the player asks of a server playback; the test sets its title, timeline and status.</summary>
internal sealed class FakeServerPlayback : IServerPlayback
{
    public event EventHandler<ServerPlaybackStatus>? StatusChanged;

    public ServerPlaybackStatus Status { get; private set; } = new(ServerPlaybackPhase.Preparing);

    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public double TimelineOffsetSeconds { get; set; }

    public double? DurationSeconds { get; set; }

    public PlaybackPreferences Preferences { get; set; } = PlaybackPreferences.Default;

    public PlayRequest? Started { get; private set; }

    public List<double> Seeks { get; } = [];

    public int Stops { get; private set; }

    /// <summary>What <see cref="StopAsync"/> returns: a pending task keeps the stop on its way.</summary>
    public Task StopResult { get; set; } = Task.CompletedTask;

    public bool Disposed { get; private set; }

    public Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        Started = request;
        return Task.CompletedTask;
    }

    public void Seek(double sourceSeconds) => Seeks.Add(sourceSeconds);

    public Task StopAsync()
    {
        Stops++;
        return StopResult;
    }

    public void Raise(ServerPlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
