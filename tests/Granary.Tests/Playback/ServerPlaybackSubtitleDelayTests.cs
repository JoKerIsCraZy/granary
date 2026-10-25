using System.Text.Json;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>The viewer's subtitle delay (player.subtitle_sync_ms), moved and saved as Silo's web player does.</summary>
public sealed class ServerPlaybackSubtitleDelayTests
{
    private const string DelayPath = "api/v2/settings/values/player.subtitle_sync_ms";

    private readonly ServerPlaybackFixture _fixture = new();

    public ServerPlaybackSubtitleDelayTests()
    {
        _fixture.Silo.On(HttpMethod.Put, DelayPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Delete, DelayPath, ScriptedSilo.NoContent());
    }

    private void SavedDelay(int milliseconds, string source) =>
        _fixture.Silo.On(HttpMethod.Get, "api/v2/settings/values/effective", Ok($$"""
            { "items": [ { "key": "player.subtitle_sync_ms", "value": {{milliseconds}}, "source": "{{source}}" } ], "revision": 4 }
            """));

    [Fact]
    public async Task TheProfilesDelay_IsWhereTheViewerStarts()
    {
        SavedDelay(-400, "profile");

        var playback = await _fixture.PlayingAsync();

        Assert.Equal(-400, playback.SubtitleDelayMs);
    }

    [Fact]
    public async Task ADelay_MovesSubtitlesAtOnce_AndIsSavedOnceTheNudgesSettle()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SetSubtitleDelay(100);
        playback.SetSubtitleDelay(200);
        playback.SetSubtitleDelay(300);
        Assert.Equal(0.3, _fixture.Engine.SubtitleDelay, 3);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, DelayPath));

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.StopAsync();

        var put = Assert.Single(_fixture.Silo.SentTo(HttpMethod.Put, DelayPath));
        Assert.Equal("?scope=profile", put.Uri.Query);
        Assert.Equal("""{"value":300}""", JsonDocument.Parse(put.Body!).RootElement.GetRawText());
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Delete, DelayPath));
    }

    [Fact]
    public async Task ADelayStillSettling_IsSavedWhenPlaybackStops()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SetSubtitleDelay(-500);
        await playback.StopAsync();

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Put, DelayPath));
    }

    [Fact]
    public async Task ADelayThisDeviceKeptForItself_IsClearedOnce_SoTheProfilesValueCounts()
    {
        SavedDelay(250, "profile_device");
        var playback = await _fixture.PlayingAsync();

        playback.SetSubtitleDelay(100);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        playback.SetSubtitleDelay(0);
        await playback.StopAsync();

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Put, DelayPath).Count);
        Assert.Equal("?scope=profile_device", Assert.Single(_fixture.Silo.SentTo(HttpMethod.Delete, DelayPath)).Uri.Query);
    }

    [Fact]
    public async Task ADelay_StaysWithinTenSeconds()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SetSubtitleDelay(25_000);

        Assert.Equal(10_000, playback.SubtitleDelayMs);
    }

    [Fact]
    public async Task ASidecarsDelay_AddsTheViewersToTheStreamsOwn()
    {
        // The transcode's stream starts at 598 s of the title; its sidecar cues are the title's time.
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));
        await Until(() => _fixture.Engine.SubtitleStreams.Contains(3), "the sidecar shows");

        playback.SetSubtitleDelay(500);

        Assert.Equal(-597.5, _fixture.Engine.SubtitleDelay, 3);
    }
}
