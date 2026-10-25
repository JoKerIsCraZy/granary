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

        Assert.Equal(
            [
                "?keys=playback.intro_skip_mode&keys=playback.auto_skip_credits&keys=playback.auto_skip_recap&keys=playback.auto_play_next"
                    + "&keys=playback.next_up_prompt_seconds",
                "?keys=playback.preferred_quality&keys=playback.max_bitrate_kbps&keys=player.video_skip_back_seconds&keys=player.video_skip_forward_seconds",
                "?keys=playback.subtitle_appearance&keys=player.subtitle_sync_ms",
            ],
            _silo.SentTo(HttpMethod.Get, EffectivePath).Select(r => r.Uri.Query).Order(StringComparer.Ordinal));
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
    public async Task Read_TakesTheSubtitleStyleAndDelay()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.subtitle_appearance", "source": "profile", "value": {
                  "fontSize": "xlarge", "fontFamily": "serif", "fontColor": "#FFFF00", "textOpacity": 80, "backgroundColor": "#101010",
                  "backgroundStyle": "box", "backgroundOpacity": 60, "textOutline": true, "textOutlineColor": "#222222", "position": "top" } },
                { "key": "player.subtitle_sync_ms", "value": -250, "source": "profile_device" }
              ],
              "revision": 3
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(new SubtitleAppearance(68, "serif", "#ffff00", 80, "#101010", SubtitleBackground.Box, 60, true, "#222222", SubtitlePosition.Top),
            preferences.Subtitles);
        Assert.Equal(-250, preferences.SubtitleDelayMs);
        Assert.True(preferences.SubtitleDelayOnDevice);
    }

    [Fact]
    public async Task Read_TakesTheMarkerSettings()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.intro_skip_mode", "value": "always", "source": "profile_device" },
                { "key": "playback.auto_skip_credits", "value": true, "source": "profile" },
                { "key": "playback.auto_skip_recap", "value": true, "source": "profile" },
                { "key": "playback.auto_play_next", "value": false, "source": "profile" },
                { "key": "playback.next_up_prompt_seconds", "value": 45, "source": "profile" }
              ],
              "revision": 7
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(new MarkerSettings(MarkerSettings.Always, true, true, false, 45), preferences.Markers);
    }

    [Fact]
    public async Task Read_AnUnknownIntroMode_NeitherPromptsNorSkips_AndOddValuesKeepTheDefaults()
    {
        // Silo's web player treats a mode it does not know as off.
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.intro_skip_mode", "value": "sometimes" },
                { "key": "playback.auto_skip_credits", "value": "yes" },
                { "key": "playback.next_up_prompt_seconds", "value": 500 }
              ],
              "revision": 7
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Never }, preferences.Markers);
    }

    [Fact]
    public async Task Read_ServerWithoutTheMarkerSettings_AsksAndOffersTheNextEpisode()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [], "revision": 1 }"""));

        Assert.Equal(MarkerSettings.Default, (await Create().ReadAsync(ScriptedSilo.Base, Token)).Markers);
    }

    [Fact]
    public async Task Read_SanitizesABrokenSubtitleStyle()
    {
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, """
            {
              "items": [
                { "key": "playback.subtitle_appearance", "value": { "fontSize": "huge", "fontColor": "red", "textOpacity": 0, "backgroundOpacity": 140,
                  "position": "middle" } },
                { "key": "player.subtitle_sync_ms", "value": 99000 }
              ],
              "revision": 3
            }
            """));

        var preferences = await Create().ReadAsync(ScriptedSilo.Base, Token);

        Assert.Equal(SubtitleAppearance.Default with { TextOpacity = 1, BackgroundOpacity = 100 }, preferences.Subtitles);
        Assert.Equal(10_000, preferences.SubtitleDelayMs);
    }

    [Fact]
    public void Default_IsTheContractsDefaults()
    {
        Assert.Equal(new PlaybackPreferences("auto", null, 10, 30), PlaybackPreferences.Default);
    }
}
