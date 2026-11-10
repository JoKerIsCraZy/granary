using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>What makes Home's Continue Watching and Next Up stale.</summary>
public sealed class ViewerStateChangesTests
{
    private readonly PlaybackStops _stops = new();

    [Fact]
    public void AReport_CountsAndTells()
    {
        var changes = new ViewerStateChanges(_stops);
        int told = 0;
        changes.Changed += (_, _) => told++;

        changes.Report();
        changes.Report();

        Assert.Equal(2, changes.Version);
        Assert.Equal(2, told);
    }

    [Fact]
    public void ACatalogReport_CountsToo_AndSaysItIsTheCatalogsOnly()
    {
        var changes = new ViewerStateChanges(_stops);
        List<bool> catalogOnly = [];
        changes.Changed += (_, change) => catalogOnly.Add(change.IsCatalogOnly);

        changes.ReportCatalog();
        changes.Report();

        Assert.Equal(2, changes.Version);
        Assert.Equal([true, false], catalogOnly);
    }

    [Fact]
    public void IsPlaying_LastsFromAPlaybacksStartUntilItsStopEnded()
    {
        var changes = new ViewerStateChanges(_stops);

        var playing = _stops.Playing();
        Assert.True(changes.IsPlaying);
        playing.Dispose();

        Assert.False(changes.IsPlaying);
    }

    [Fact]
    public async Task APlaybackStop_IsAChange_OnceItEnded()
    {
        // The stop carries where the viewer left off: Continue Watching knows it only after the server has it.
        var changes = new ViewerStateChanges(_stops);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _stops.Track(stop.Task);
        Assert.Equal(0, changes.Version);
        stop.SetResult();

        await Auth.AuthPipeline.Eventually(() => changes.Version == 1);
    }

    [Fact]
    public void AStopThatFailed_IsAChangeToo()
    {
        var changes = new ViewerStateChanges(_stops);

        _stops.Track(Task.FromException(new HttpRequestException("gone")));

        Assert.Equal(1, changes.Version);
    }
}
