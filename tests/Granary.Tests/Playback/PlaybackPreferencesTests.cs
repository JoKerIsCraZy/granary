using System.Net;
using Granary.Core.Api.Settings;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The profile settings playback uses (docs/api-contract.md, 6.6), read through the effective-settings call.</summary>
public sealed class PlaybackPreferencesTests
{
    private const string EffectivePath = "api/v2/settings/values/effective";

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PlaybackPreferencesReader Create() => new(new SettingsApi(_silo.Http), _logs.For<PlaybackPreferencesReader>());

    [Fact]
    public async Task Read_AsksForTheKeysPlaybackUses()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [], "revision": 1 }"""));

        await Create().ReadAsync(ScriptedSilo.Base, Token);

        var sent = Assert.Single(_silo.SentTo(HttpMethod.Get, EffectivePath));
        Assert.Equal(
            "?keys=playback.preferred_quality&keys=playback.max_bitrate_kbps&keys=player.video_skip_back_seconds&keys=player.video_skip_forward_seconds",
            sent.Uri.Query);
    }

    [Fact]
    public async Task Read_TakesTheProfilesValues()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.preferred_quality", "value": "1080P", "source": "profile", "definition_revision": 3 },
                { "key": "playback.max_bitrate_kbps", "value": 8000, "source": "profile_device" },
                { "key": "player.video_skip_back_seconds", "value": 15, "source": "profile" },
                { "key": "player.video_skip_forward_seconds", "value": 45, "source": "profile" },
                { "key": "something.else", "value": { "nested": true } }
              ],
              "revision": 8
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(new PlaybackPreferences("1080p", 8000, 15, 45), preferences);
    }

    [Fact]
    public async Task Read_KeepsTheDefaultsForMissingNullOrUnusableValues()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.preferred_quality", "value": "", "source": "default" },
                { "key": "playback.max_bitrate_kbps", "value": null, "source": "default" },
                { "key": "player.video_skip_back_seconds", "value": -4, "source": "profile" },
                { "key": "player.video_skip_forward_seconds", "value": "thirty", "source": "profile" }
              ],
              "revision": 2
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(PlaybackPreferences.Default, preferences);
    }

    [Fact]
    public async Task Read_FallsBackToTheDefaults_WhenTheServerRefusesTheKeys()
    {
        // An older server that does not know a key fails the whole batch.
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "validation_failed"));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(PlaybackPreferences.Default, preferences);
        Assert.Contains(_logs.Lines, l => l.Contains("settings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Read_FallsBackToTheDefaults_WhenTheServerIsUnreachable()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());

        Assert.Equal(PlaybackPreferences.Default, await Create().ReadAsync(ScriptedSilo.Base, Token));
    }

    [Fact]
    public void Default_IsTheContractsDefaults()
    {
        Assert.Equal(new PlaybackPreferences("auto", null, 10, 30), PlaybackPreferences.Default);
    }
}
