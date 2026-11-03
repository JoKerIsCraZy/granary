using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// The detail page's track choices on their way to the server, in the order they were made (Apple's TrackSelectionPersistence),
/// each for the server and profile that made it.
/// </summary>
public sealed class TrackChoiceWritesTests
{
    private const string AudioPrefs = "api/v2/audio-prefs/movie-1";
    private const string SubtitlePrefs = "api/v2/subtitle-prefs/movie-1";

    private readonly ManualTimeProvider _time = new(ManualTimeProvider.DefaultStart);
    private readonly LogSink _logs = new();
    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "7",
    };

    private TrackChoiceWrites Create() => new(_session, _time, _logs.For<TrackChoiceWrites>());

    // An audio pick saved through the API, as the detail page saves it: for the owner the queue hands it.
    private Task<TrackWriteOutcome> SaveAudio(TrackChoiceWrites writes) =>
        writes.RunAsync("audio", (owner, cancellationToken) => new TrackPreferencesApi(_silo.Http).SaveAudioAsync(owner.Server, "movie-1",
            owner.ProfileId, AudioPreference.Of(new WatchAudioTrack { Language = "ger", Codec = "ac3" }, 1), cancellationToken));

    // A write the test lets go when it wants; it ignores cancellation, as a stuck operation might.
    private static (Func<TrackWriteOwner, CancellationToken, Task> Write, TaskCompletionSource Gate) Held()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return ((_, _) => gate.Task, gate);
    }

    [Fact]
    public async Task AWrite_WaitsForTheOneBefore()
    {
        var writes = Create();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var pick = writes.RunAsync("audio", async (_, _) => { await first.Task; order.Add("pick"); });
        var auto = writes.RunAsync("audio", (_, _) => { order.Add("auto"); return Task.CompletedTask; });
        Assert.Empty(order);
        first.SetResult();

        Assert.Equal(TrackWriteOutcome.Saved, await pick);
        Assert.Equal(TrackWriteOutcome.Saved, await auto);
        Assert.Equal(["pick", "auto"], order);
    }

    [Fact]
    public async Task AFailedWrite_IsLogged_AndTheNextGoesOut()
    {
        var writes = Create();

        var failed = await writes.RunAsync("subtitle",
            (_, _) => throw new SiloApiException(500, new ApiProblem(ApiProblem.TypeOrigin + "internal", "internal", 500, null, null, null)));
        var next = await writes.RunAsync("subtitle", (_, _) => Task.CompletedTask);

        Assert.Equal(TrackWriteOutcome.Failed, failed);
        Assert.Equal(TrackWriteOutcome.Saved, next);
        Assert.Contains(_logs.Lines, line => line.Contains("Saving the subtitle choice failed: 500 internal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteThatHangs_HoldsAStartNoLongerThanItsTimeout()
    {
        var writes = Create();
        var hanging = writes.RunAsync("audio", (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));

        var settling = writes.SettleAsync(TestContext.Current.CancellationToken);
        Assert.False(settling.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(10));
        await settling;

        // The write gives up at the same time: a pick costs its remembering, never playback.
        Assert.Equal(TrackWriteOutcome.Failed, await hanging);
    }

    [Fact]
    public async Task AWriteThatIgnoresItsTimeout_DoesNotHoldTheNextOne()
    {
        var writes = Create();
        var (stuck, _) = Held();
        _silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());

        var first = writes.RunAsync("audio", stuck);
        var next = SaveAudio(writes);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TrackWriteOutcome.Failed, await first);
        Assert.Equal(TrackWriteOutcome.Saved, await next);
    }

    [Fact]
    public async Task AWrite_GoesOutForTheServerAndProfileThatMadeIt()
    {
        var writes = Create();
        _silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());

        Assert.Equal(TrackWriteOutcome.Saved, await SaveAudio(writes));

        var sent = Assert.Single(_silo.SentTo(HttpMethod.Put, AudioPrefs));
        Assert.Equal((SiloAuth.Profile, "7"), (sent.Auth, sent.AsProfile));
    }

    [Fact]
    public async Task AWriteQueuedBeforeAProfileSwitch_IsNotSentForTheNewProfile()
    {
        var writes = Create();
        var (held, gate) = Held();
        _silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());
        var first = writes.RunAsync("audio", held);
        var queued = SaveAudio(writes);

        _session.ProfileId = "9";
        _session.RaiseChanged();
        gate.SetResult();

        Assert.Equal(TrackWriteOutcome.Dropped, await first);
        Assert.Equal(TrackWriteOutcome.Dropped, await queued);
        Assert.Empty(_silo.SentTo(HttpMethod.Put, AudioPrefs));
        Assert.Contains(_logs.Lines, line => line.Contains("server or profile changed", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains('7', StringComparison.Ordinal) || line.Contains('9', StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteQueuedBeforeAServerSwitch_IsNotSent()
    {
        var writes = Create();
        var (held, gate) = Held();
        _silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());
        var first = writes.RunAsync("audio", held);
        var queued = SaveAudio(writes);

        _session.ActiveServer = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
        _session.RaiseChanged();
        gate.SetResult();

        Assert.Equal(TrackWriteOutcome.Dropped, await queued);
        Assert.Empty(_silo.Sent);
        Assert.Equal(TrackWriteOutcome.Dropped, await first);
    }

    [Fact]
    public async Task AWriteQueuedWithoutAProfile_IsNotSent()
    {
        _session.ProfileId = null;

        Assert.Equal(TrackWriteOutcome.Dropped, await SaveAudio(Create()));
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AStartForTheNewProfile_DoesNotWaitForTheOldProfilesWrites()
    {
        var writes = Create();
        var (held, _) = Held();
        var first = writes.RunAsync("audio", held);
        var queued = writes.RunAsync("subtitle", held);
        Assert.False(writes.SettleAsync(TestContext.Current.CancellationToken).IsCompleted);

        _session.ProfileId = "9";
        _session.RaiseChanged();

        // No time passes: the settle ends at once, and so do the old profile's writes, the stuck one too.
        Assert.True(writes.SettleAsync(TestContext.Current.CancellationToken).IsCompleted);
        Assert.Equal(TrackWriteOutcome.Dropped, await first);
        Assert.Equal(TrackWriteOutcome.Dropped, await queued);
    }

    [Fact]
    public async Task AWriteOfTheNewProfile_DoesNotQueueBehindTheOldProfilesWrites()
    {
        var writes = Create();
        var (held, _) = Held();
        _silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());
        _ = writes.RunAsync("audio", held);

        _session.ProfileId = "9";
        _session.RaiseChanged();

        Assert.Equal(TrackWriteOutcome.Saved, await SaveAudio(writes));
        Assert.Equal("9", Assert.Single(_silo.SentTo(HttpMethod.Put, AudioPrefs)).AsProfile);
    }

    [Fact]
    public async Task Forgetting_DeletesThePickOfTheTitle_ForItsProfile()
    {
        _silo.On(HttpMethod.Delete, "api/v2/audio-prefs/series-9", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, SubtitlePrefs, ScriptedSilo.NoContent());
        var api = new TrackPreferencesApi(_silo.Http);

        await api.DeleteAudioAsync(ScriptedSilo.Base, TrackPreferencesApi.KeyFor("episode", "series-9", "episode-1"), "7",
            TestContext.Current.CancellationToken);
        await api.DeleteSubtitleAsync(ScriptedSilo.Base, TrackPreferencesApi.KeyFor("movie", "series-9", "movie-1"), "7",
            TestContext.Current.CancellationToken);

        var audio = Assert.Single(_silo.SentTo(HttpMethod.Delete, "api/v2/audio-prefs/series-9"));
        Assert.Equal((SiloAuth.Profile, "7"), (audio.Auth, audio.AsProfile));
        Assert.Equal("7", Assert.Single(_silo.SentTo(HttpMethod.Delete, SubtitlePrefs)).AsProfile);
    }
}
