using System.Net;
using System.Text;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// This device's audio delay (player.audio_sync_ms), shared by Settings and the player, as Silo's Android app keeps it.
/// The clock is the test's: a save the settle timer starts is waited for through <see cref="AudioDelay.LatestSave"/>.
/// </summary>
public sealed class AudioDelayTests
{
    private const string ValuePath = "api/v2/settings/values/" + AudioDelay.Key;
    private const string EffectivePath = "api/v2/settings/values/effective";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly Uri OtherBase = new("https://other.example/silo");

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };

    public AudioDelayTests()
    {
        _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
        // The authentication handler sends a write made for a profile only as that profile.
        _silo.ActingProfile = () => _session.ActiveServer == ScriptedSilo.Server ? _session.ProfileId : "7";
    }

    private AudioDelay Create(IUiDispatcher? dispatcher = null) =>
        new(new SettingsApi(_silo.Http), _session, dispatcher ?? new ImmediateDispatcher(), _time, _logs.For<AudioDelay>());

    private IReadOnlyList<SentRequest> Saves => _silo.SentTo(HttpMethod.Put, ValuePath);

    private static int Value(SentRequest save) => JsonDocument.Parse(save.Body!).RootElement.GetProperty("value").GetInt32();

    private static Func<Task<HttpResponseMessage>> Effective(string value) => ScriptedSilo.Json(HttpStatusCode.OK,
        $$"""{ "items": [ { "key": "player.audio_sync_ms", "value": {{value}}, "source": "profile_device" } ], "revision": 3 }""");

    // Lets the settle timer start its save, and waits for that save.
    private async Task SettleAsync(AudioDelay delay)
    {
        _time.Advance(Settle);
        await delay.LatestSave;
    }

    [Fact]
    public async Task AChange_ReachesWhatPlaysAtOnce_AndIsSavedForThisDeviceAsThisProfileOnceItSettles()
    {
        var delay = Create();
        var seen = new List<int>();
        delay.Changed += (_, _) => seen.Add(delay.Milliseconds);

        delay.Set(100);
        delay.Set(150);
        delay.Set(200);

        Assert.Equal([100, 150, 200], seen);
        Assert.Empty(Saves);
        await SettleAsync(delay);
        var save = Assert.Single(Saves);
        Assert.Equal("?scope=profile_device", save.Uri.Query);
        Assert.Equal("7", save.AsProfile);
        Assert.Equal(200, Value(save));
        _time.Advance(TimeSpan.FromSeconds(5));
        await delay.LatestSave;
        Assert.Single(Saves);
    }

    [Fact]
    public void TheDelay_StaysWithinFiveSecondsEitherWay()
    {
        var delay = Create();

        delay.Set(9000);
        Assert.Equal(5000, delay.Milliseconds);
        delay.Set(-12000);
        Assert.Equal(-5000, delay.Milliseconds);
    }

    [Fact]
    public async Task TheServersValue_IsTaken_UnlessAChangeMadeHereIsStillOnItsWay()
    {
        var delay = Create();
        int changes = 0;
        delay.Changed += (_, _) => changes++;

        delay.Take(300);
        Assert.Equal(300, delay.Milliseconds);
        Assert.Equal(1, changes);
        delay.Take(300);
        Assert.Equal(1, changes);

        delay.Set(100);
        delay.Take(300);
        Assert.Equal(100, delay.Milliseconds);

        await SettleAsync(delay);
        Assert.Single(Saves);
        delay.Take(250);
        Assert.Equal(250, delay.Milliseconds);
    }

    [Theory]
    [InlineData(503, "unavailable")]
    [InlineData(412, "precondition_failed")]
    [InlineData(422, "validation_failed")]
    public async Task ASaveTheServerRefuses_IsLogged_KeepsTheChoiceForThisRun_AndTheCloseTriesAgain(int status, string code)
    {
        _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.Problem(status, code), ScriptedSilo.NoContent());
        var delay = Create();

        delay.Set(-200);
        await SettleAsync(delay);
        Assert.Single(Saves);
        Assert.Contains(_logs.Lines, l => l.Contains("Saving the audio delay failed", StringComparison.Ordinal));

        // A title read meanwhile still plays with the viewer's choice.
        delay.Take(0);
        Assert.Equal(-200, delay.Milliseconds);

        await delay.SaveNowAsync();
        Assert.Equal(2, Saves.Count);
        Assert.Equal(-200, Value(Saves[1]));
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task AChangeWaitingAtTheClose_IsSavedAtOnce_AndOnlyOnce()
    {
        var delay = Create();

        delay.Set(250);
        await delay.SaveNowAsync();
        await delay.SaveNowAsync();

        Assert.Equal(250, Value(Assert.Single(Saves)));
        _time.Advance(TimeSpan.FromSeconds(2));
        await delay.SaveNowAsync();
        Assert.Single(Saves);
    }

    [Fact]
    public async Task ASettleTimerThatWasDueAsALaterChangeCame_SavesNothingEarly()
    {
        var dispatcher = new QueuedDispatcher();
        var delay = Create(dispatcher);

        delay.Set(100);
        // Its timer is due: its save waits on the busy UI thread while the next change comes.
        _time.Advance(Settle);
        delay.Set(200);
        dispatcher.RunAll();
        Assert.Empty(Saves);

        _time.Advance(Settle);
        dispatcher.RunAll();
        await delay.LatestSave;
        Assert.Equal(200, Value(Assert.Single(Saves)));
    }

    [Fact]
    public async Task AChangeMadeJustBeforeASwitchToAnotherServer_StillReachesItsOwnServer_AsItsProfile()
    {
        var other = ScriptedSilo.Entry("srv2", OtherBase);
        var delay = Create();

        delay.Set(400);
        _session.ActiveServer = other;
        _session.ProfileId = "3";
        await SettleAsync(delay);

        var save = Assert.Single(Saves);
        Assert.Equal(400, Value(save));
        Assert.Equal("7", save.AsProfile);
        Assert.Equal(ScriptedSilo.Url(ValuePath).AbsolutePath, save.Uri.AbsolutePath);
    }

    [Fact]
    public async Task AChangeForAProfileItsServerNoLongerActsAs_IsNeverSentAsTheNewOne()
    {
        var delay = Create();

        delay.Set(400);
        _session.ProfileId = "8";
        await SettleAsync(delay);

        Assert.Empty(Saves);
        Assert.Contains(_logs.Lines, l => l.Contains("profile changed", StringComparison.Ordinal));
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task AChangeLeftWaitingByAnotherProfile_GoesOutBeforeTheNewProfilesChangeTakesItsPlace()
    {
        // Each server keeps its own sign-in, with its own profile.
        _silo.ActingProfile = null;
        var other = ScriptedSilo.Entry("srv2", OtherBase);
        var delay = Create();

        delay.Set(400);
        _session.ActiveServer = other;
        _session.ProfileId = "3";
        _silo.On(HttpMethod.Put, ScriptedSilo.Url(ValuePath, OtherBase), ScriptedSilo.NoContent());
        delay.Set(-100);
        await delay.SaveNowAsync();
        await SettleAsync(delay);

        Assert.Equal([400], Saves.Select(Value));
        Assert.Equal("7", Saves[0].AsProfile);
        Assert.Contains(_silo.Sent, r => r.Method == HttpMethod.Put && r.Uri.Host == OtherBase.Host && Value(r) == -100 && r.AsProfile == "3");
    }

    [Fact]
    public async Task ASlowSaveForAnotherServer_DoesNotHoldBackTheSaveOfTheProfileActingNow()
    {
        // Each server keeps its own sign-in, with its own profile.
        _silo.ActingProfile = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Put, ValuePath, async () =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        _silo.On(HttpMethod.Put, ScriptedSilo.Url(ValuePath, OtherBase), ScriptedSilo.NoContent());
        var delay = Create();

        delay.Set(100);
        var hanging = delay.SaveNowAsync();
        await ServerPlaybackFixture.Until(() => Saves.Count == 1, "the first server's save goes out");
        _session.ActiveServer = ScriptedSilo.Entry("srv2", OtherBase);
        _session.ProfileId = "3";
        delay.Set(-100);
        var saving = delay.SaveNowAsync();

        await ServerPlaybackFixture.Until(() => _silo.Sent.Any(r => r.Method == HttpMethod.Put && r.Uri.Host == OtherBase.Host),
            "the acting profile's save goes out while the other one hangs");
        Assert.False(hanging.IsCompleted);
        var sent = _silo.Sent.Single(r => r.Method == HttpMethod.Put && r.Uri.Host == OtherBase.Host);
        Assert.Equal(-100, Value(sent));
        Assert.Equal("3", sent.AsProfile);
        gate.SetResult();
        await Task.WhenAll(hanging, saving);
    }

    [Fact]
    public async Task TheClose_WaitsForEveryProfilesSaves_NotOnlyTheActingOnes()
    {
        // Each server keeps its own sign-in, with its own profile.
        _silo.ActingProfile = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Put, ValuePath, async () =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        _silo.On(HttpMethod.Put, ScriptedSilo.Url(ValuePath, OtherBase), ScriptedSilo.NoContent());
        var delay = Create();
        delay.Set(100);
        _ = delay.SaveNowAsync();
        await ServerPlaybackFixture.Until(() => Saves.Count == 1, "the first server's save goes out");
        _session.ActiveServer = ScriptedSilo.Entry("srv2", OtherBase);
        _session.ProfileId = "3";
        delay.Set(-100);

        var closing = delay.SaveAllNowAsync();

        await ServerPlaybackFixture.Until(() => _silo.Sent.Any(r => r.Method == HttpMethod.Put && r.Uri.Host == OtherBase.Host),
            "the waiting change goes out at the close");
        Assert.False(closing.IsCompleted);
        gate.SetResult();
        await closing;
        Assert.Equal(100, Value(Assert.Single(Saves)));
    }

    [Fact]
    public async Task ASignOutWhileAChangeWaits_SendsNothing_LogsIt_AndLeavesNothingWaiting()
    {
        var delay = Create();

        delay.Set(400);
        _session.State = SessionState.SignedOut;
        _session.ProfileId = null;
        await SettleAsync(delay);

        Assert.Empty(Saves);
        Assert.Contains(_logs.Lines, l => l.Contains("profile changed", StringComparison.Ordinal));
        _session.State = SessionState.Ready;
        _session.ProfileId = "7";
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task AChangeMadeWithoutASession_IsNotSaved_AndLeavesNothingWaiting()
    {
        _session.State = SessionState.NoServer;
        _session.ActiveServer = null;
        _session.ProfileId = null;
        var delay = Create();

        delay.Set(300);
        Assert.Equal(300, delay.Milliseconds);
        _time.Advance(Settle);
        await delay.SaveNowAsync();

        Assert.Empty(Saves);
        _session.State = SessionState.Ready;
        _session.ActiveServer = ScriptedSilo.Server;
        _session.ProfileId = "7";
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public void AValueThatCouldNotBeReadForATitle_StaysWhenItIsTheActingProfiles()
    {
        var delay = Create();
        delay.Take(200);

        delay.TakeUnread();

        Assert.Equal(200, delay.Milliseconds);
    }

    [Fact]
    public void AValueThatCouldNotBeReadForATitle_IsNoneWhenTheOneKnownIsAnotherProfiles()
    {
        var delay = Create();
        delay.Set(200);
        _session.ProfileId = "8";

        delay.TakeUnread();

        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task AChangeWaitingBehindASlowSave_IsNotSavedForAProfileThatTookOverMeanwhile()
    {
        // Saves of one profile go one after another, so the latest value is the one its server keeps.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Put, ValuePath, async () =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var delay = Create();

        delay.Set(100);
        var first = delay.SaveNowAsync();
        await ServerPlaybackFixture.Until(() => Saves.Count == 1, "the first save goes out");
        delay.Set(200);
        var second = delay.SaveNowAsync();
        _session.ProfileId = "8";
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(100, Value(Assert.Single(Saves)));
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task ARead_ThatAnswersAfterAChangeMadeHere_LeavesTheChange()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, EffectivePath, async () =>
        {
            await gate.Task;
            return await Effective("300")();
        });
        var delay = Create();

        var read = delay.ReadAsync(TestContext.Current.CancellationToken);
        delay.Set(100);
        await delay.SaveNowAsync();
        gate.SetResult();
        await read;

        Assert.Equal(100, delay.Milliseconds);
    }

    [Fact]
    public async Task ARead_ForAProfileThatIsNoLongerTheOneActing_IsNotTaken()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, EffectivePath, async () =>
        {
            await gate.Task;
            return await Effective("300")();
        });
        var delay = Create();

        var read = delay.ReadAsync(TestContext.Current.CancellationToken);
        _session.ProfileId = "8";
        gate.SetResult();
        await read;

        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task ARead_TheServerRefuses_SaysSo()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "validation_failed"));
        var delay = Create();

        var refused = await Assert.ThrowsAsync<SiloApiException>(() => delay.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(422, refused.Status);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Theory]
    [InlineData("-250", -250)]
    [InlineData("99000", 5000)]
    [InlineData("-7000", -5000)]
    [InlineData("120.4", 120)]
    [InlineData("\"late\"", 0)]
    [InlineData("null", 0)]
    public async Task Read_TakesThisDevicesValue_WithinTheRange(string value, int expected)
    {
        _silo.On(HttpMethod.Get, EffectivePath, Effective(value));
        var delay = Create();

        await delay.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, delay.Milliseconds);
        Assert.Equal("?keys=player.audio_sync_ms", Assert.Single(_silo.SentTo(HttpMethod.Get, EffectivePath)).Uri.Query);
    }
}
