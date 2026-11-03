using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The detail page's track choices on their way to the server, in the order they were made (Apple's TrackSelectionPersistence).</summary>
public sealed class TrackChoiceWritesTests
{
    private readonly ManualTimeProvider _time = new(ManualTimeProvider.DefaultStart);
    private readonly LogSink _logs = new();

    private TrackChoiceWrites Create() => new(_time, _logs.For<TrackChoiceWrites>());

    [Fact]
    public async Task AWrite_WaitsForTheOneBefore()
    {
        var writes = Create();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var pick = writes.RunAsync("audio", async _ => { await first.Task; order.Add("pick"); });
        var auto = writes.RunAsync("audio", _ => { order.Add("auto"); return Task.CompletedTask; });
        Assert.False(writes.IsIdle);
        first.SetResult();

        Assert.True(await pick);
        Assert.True(await auto);
        Assert.Equal(["pick", "auto"], order);
        Assert.True(writes.IsIdle);
    }

    [Fact]
    public async Task AFailedWrite_IsLogged_AndTheNextGoesOut()
    {
        var writes = Create();

        bool failed = await writes.RunAsync("subtitle",
            _ => throw new SiloApiException(500, new ApiProblem(ApiProblem.TypeOrigin + "internal", "internal", 500, null, null, null)));
        bool next = await writes.RunAsync("subtitle", _ => Task.CompletedTask);

        Assert.False(failed);
        Assert.True(next);
        Assert.Contains(_logs.Lines, line => line.Contains("Saving the subtitle choice failed: 500 internal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteThatHangs_HoldsAStartNoLongerThanItsTimeout()
    {
        var writes = Create();
        var hanging = writes.RunAsync("audio", cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken));

        var settling = writes.SettleAsync(TestContext.Current.CancellationToken);
        Assert.False(settling.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(10));
        await settling;

        // The write gives up at the same time: a pick costs its remembering, never playback.
        Assert.False(await hanging);
    }

    [Fact]
    public async Task Forgetting_DeletesThePickOfTheTitle()
    {
        var silo = new ScriptedSilo();
        silo.On(HttpMethod.Delete, "api/v2/audio-prefs/series-9", ScriptedSilo.NoContent());
        silo.On(HttpMethod.Delete, "api/v2/subtitle-prefs/movie-1", ScriptedSilo.NoContent());
        var api = new TrackPreferencesApi(silo.Http);

        await api.DeleteAudioAsync(ScriptedSilo.Base, TrackPreferencesApi.KeyFor("episode", "series-9", "episode-1"), TestContext.Current.CancellationToken);
        await api.DeleteSubtitleAsync(ScriptedSilo.Base, TrackPreferencesApi.KeyFor("movie", "series-9", "movie-1"), TestContext.Current.CancellationToken);

        Assert.Equal(SiloAuth.Profile, silo.SentTo(HttpMethod.Delete, "api/v2/audio-prefs/series-9").Single().Auth);
        Assert.Single(silo.SentTo(HttpMethod.Delete, "api/v2/subtitle-prefs/movie-1"));
    }
}
