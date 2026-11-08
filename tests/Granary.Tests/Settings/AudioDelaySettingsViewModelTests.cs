using System.Net;
using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>Settings → Playback → Sound: this device's audio sync offset, as the web's device settings show it.</summary>
public sealed class AudioDelaySettingsViewModelTests
{
    private const string ValuePath = "api/v2/settings/values/" + AudioDelay.Key;
    private const string EffectivePath = "api/v2/settings/values/effective";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly AudioDelay _delay;

    public AudioDelaySettingsViewModelTests()
    {
        _delay = new AudioDelay(new SettingsApi(_silo.Http),
            new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" },
            new ImmediateDispatcher(), _time, _logs.For<AudioDelay>());
        _silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void Stored(string value) => _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK,
        $$"""{ "items": [ { "key": "player.audio_sync_ms", "value": {{value}}, "source": "profile_device" } ], "revision": 2 }"""));

    private AudioDelaySettingsViewModel Create() => new(_delay, _logs.For<AudioDelaySettingsViewModel>());

    private IReadOnlyList<SentRequest> Saves => _silo.SentTo(HttpMethod.Put, ValuePath);

    [Fact]
    public async Task TheRow_ShowsThisDevicesOffset_OnceRead()
    {
        Stored("-237");
        using var page = Create();
        Assert.False(page.IsLoaded);

        await page.LoadAsync(Token);

        Assert.True(page.IsLoaded);
        Assert.Equal(-237, page.Milliseconds);
        Assert.Equal("-237 milliseconds", page.ValueText);
        Assert.Empty(Saves);
    }

    [Fact]
    public async Task MovingTheSlider_ChangesTheDelayAtOnce_InTheAppsSteps_AndSavesItOnceItSettles()
    {
        Stored("0");
        using var page = Create();
        await page.LoadAsync(Token);

        page.Milliseconds = 120;
        page.Milliseconds = 137;

        Assert.Equal(150, page.Milliseconds);
        Assert.Equal("150 milliseconds", page.ValueText);
        Assert.Equal(150, _delay.Milliseconds);
        _time.Advance(TimeSpan.FromSeconds(1));
        await ServerPlaybackFixture.Until(() => Saves.Count == 1, "the offset is saved");
        Assert.Equal(150, JsonDocument.Parse(Saves[0].Body!).RootElement.GetProperty("value").GetInt32());
        Assert.Equal("?scope=profile_device", Saves[0].Uri.Query);
    }

    [Fact]
    public async Task AChangeMadeInThePlayer_ShowsInTheRow_UntilThePageLeaves()
    {
        Stored("0");
        var page = Create();
        await page.LoadAsync(Token);

        _delay.Set(-300);
        Assert.Equal(-300, page.Milliseconds);

        page.Dispose();
        _delay.Set(200);
        Assert.Equal(-300, page.Milliseconds);
    }

    [Fact]
    public async Task AnOffsetThatCouldNotBeRead_IsNotOffered()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());
        using var page = Create();

        await page.LoadAsync(Token);

        Assert.False(page.IsLoaded);
        page.Milliseconds = 500;
        Assert.Equal(0, _delay.Milliseconds);
        Assert.Contains(_logs.Lines, l => l.Contains("audio sync offset", StringComparison.OrdinalIgnoreCase));
    }
}

