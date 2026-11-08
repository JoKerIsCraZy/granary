using Granary.Core.Playback;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A title's local extra plays as any title of the server does. The server's watch detail names a series' extra with its
/// series, but an extra is no episode: nothing plays next after it, as in the Android and Apple apps, whose next episode
/// needs a season and an episode number.
/// </summary>
public sealed class ExtraPlaybackTests
{
    private const string ExtraId = "extra-71";

    private readonly ServerPlaybackFixture _fixture = new();

    [Fact]
    public async Task ASeriesExtra_IsNoEpisode_SoNothingPlaysNextAfterIt()
    {
        string extra = PlaybackPayloads.WatchStateOk
            .Replace("\"content_id\": \"movie:heat-1995\"", "\"content_id\": \"" + ExtraId + "\"", StringComparison.Ordinal)
            .Replace("\"type\": \"movie\"", "\"type\": \"extra\", \"series_id\": \"series-tvdb-296762\", \"series_title\": \"Severance\"",
                StringComparison.Ordinal);
        Assert.Contains("\"type\": \"extra\"", extra, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + ExtraId, Ok(extra));

        var playback = await _fixture.StartAsync(PlayRequest.FromStart(ExtraId));

        Assert.Single(_fixture.Engine.Loaded);
        Assert.Null(playback.Episode);
        Assert.Equal(("Heat", "1995"), (playback.Title, playback.Subtitle));
        Assert.Equal(PlaybackMediaKind.Other, playback.MediaInfo.Kind);
    }
}
