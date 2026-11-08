using System.Net;
using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>This device's audio delay (player.audio_sync_ms), shared by Settings and the player, as Silo's Android app keeps it.</summary>
public sealed class AudioDelayTests
{
    private const string ValuePath = "api/v2/settings/values/" + AudioDelay.Key;
    private const string EffectivePath = "api/v2/settings/values/effective";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };

    public AudioDelayTests() => _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());

    private AudioDelay Create() => new(new SettingsApi(_silo.Http), _session, new ImmediateDispatcher(), _time, _logs.For<AudioDelay>());

    private IReadOnlyList<SentRequest> Saves => _silo.SentTo(HttpMethod.Put, ValuePath);

    private static int Value(SentRequest save) => JsonDocument.Parse(save.Body!).RootElement.GetProperty("value").GetInt32();

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AChange_ReachesWhatPlaysAtOnce_AndIsSavedForThisDeviceOnceItSettles()
    {
        var delay = Create();
        var seen = new List<int>();
        delay.Changed += (_, _) => seen.Add(delay.Milliseconds);

        delay.Set(100);
        delay.Set(150);
        delay.Set(200);

        Assert.Equal([100, 150, 200], seen);
        Assert.Empty(Saves);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        var save = Assert.Single(Saves);
        Assert.Equal("?scope=profile_device", save.Uri.Query);
        Assert.Equal(200, Value(save));
        await AdvanceAsync(TimeSpan.FromSeconds(5));
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

        await AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Single(Saves);
        delay.Take(250);
        Assert.Equal(250, delay.Milliseconds);
    }

    [Fact]
    public async Task AFailedSave_KeepsTheChoiceForThisRun_AndTheCloseTriesAgain()
    {
        _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.NoContent());
        var delay = Create();

        delay.Set(-200);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Single(Saves);
        Assert.Contains(_logs.Lines, l => l.Contains("audio delay", StringComparison.OrdinalIgnoreCase));

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

        Assert.Equal(250, Value(Assert.Single(Saves)));
        await AdvanceAsync(TimeSpan.FromSeconds(2));
        await delay.SaveNowAsync();
        Assert.Single(Saves);
    }

    [Fact]
    public async Task AChangeMadeForAnotherProfile_IsNotSaved_AndTheNewProfilesValueIsTaken()
    {
        var delay = Create();

        delay.Set(400);
        _session.ProfileId = "8";
        await AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(Saves);
        delay.Take(0);
        Assert.Equal(0, delay.Milliseconds);
    }

    [Fact]
    public async Task AChangeWaitingBehindASlowSave_IsNotSavedForAProfileThatTookOverMeanwhile()
    {
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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "items": [ { "key": "player.audio_sync_ms", "value": 300 } ], "revision": 3 }""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "items": [ { "key": "player.audio_sync_ms", "value": 300 } ], "revision": 3 }""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var delay = Create();

        var read = delay.ReadAsync(TestContext.Current.CancellationToken);
        _session.ProfileId = "8";
        gate.SetResult();
        await read;

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
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "items": [ { "key": "player.audio_sync_ms", "value": {{value}}, "source": "profile_device" } ], "revision": 3 }"""));
        var delay = Create();

        await delay.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, delay.Milliseconds);
        Assert.Equal("?keys=player.audio_sync_ms", Assert.Single(_silo.SentTo(HttpMethod.Get, EffectivePath)).Uri.Query);
    }
}
