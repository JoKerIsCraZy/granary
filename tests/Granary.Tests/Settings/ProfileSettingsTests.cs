using System.Net;
using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The settings pages' reads and writes, as the web's useProfileDefaultWriter makes them.</summary>
public sealed class ProfileSettingsTests
{
    private const string Effective = "api/v2/settings/values/effective";

    private readonly ScriptedSilo _silo = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ProfileSettings Create() => new(new SettingsApi(_silo.Http),
        new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" },
        new LogSink().For<ProfileSettings>());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ASave_WritesTheProfilesValue_AndClearsTheDevicesThatWouldShadowIt()
    {
        _silo.On(HttpMethod.Put, "api/v2/settings/values/playback.auto_skip_credits", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, "api/v2/settings/values/playback.auto_skip_credits", ScriptedSilo.Problem(404, "not_found"));

        await Create().SaveAsync(SettingKeys.AutoSkipCredits, Json("false"), clearDevice: true, Token);

        Assert.Equal(["PUT ?scope=profile", "DELETE ?scope=profile_device"], _silo.Sent.Select(r => $"{r.Method} {r.Uri.Query}"));
        Assert.Equal("{\"value\":false}", _silo.Sent[0].Body);
    }

    [Fact]
    public async Task AProfileOnlySetting_IsNeverClearedOnTheDevice()
    {
        _silo.On(HttpMethod.Put, "api/v2/settings/values/player.video_skip_back_seconds", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, "api/v2/settings/values/player.video_skip_back_seconds", ScriptedSilo.NoContent());
        var settings = Create();

        await settings.SaveAsync(SettingKeys.SkipBackSeconds, Json("15"), clearDevice: true, Token);
        await settings.ResetAsync(SettingKeys.SkipBackSeconds, Token);

        Assert.Equal(["PUT ?scope=profile", "DELETE ?scope=profile"], _silo.Sent.Select(r => $"{r.Method} {r.Uri.Query}"));
    }

    [Fact]
    public async Task AReset_ClearsTheProfilesValueAndTheDevices()
    {
        _silo.On(HttpMethod.Delete, "api/v2/settings/values/playback.max_bitrate_kbps", ScriptedSilo.NoContent(), ScriptedSilo.Problem(404, "not_found"));

        await Create().ResetAsync(SettingKeys.MaxBitrateKbps, Token);

        Assert.Equal(["?scope=profile", "?scope=profile_device"], _silo.Sent.Select(r => r.Uri.Query));
    }

    [Fact]
    public async Task AServerThatRefusesTheBatch_IsAskedKeyByKey_AndUnknownKeysAreLeftOut()
    {
        _silo.On(HttpMethod.Get, Effective, ScriptedSilo.Problem(422, "validation_failed"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ { "key": "playback.intro_skip_mode", "value": "ask", "source": "default" } ], "revision": 1 }"""),
            ScriptedSilo.Problem(422, "validation_failed"));

        var values = await Create().ReadAsync([SettingKeys.IntroSkipMode, SettingKeys.BufferingQualityMode], Token);

        Assert.Equal([SettingKeys.IntroSkipMode], values.Keys);
        Assert.Equal(3, _silo.Sent.Count);
    }
}
