using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Tests.Catalog;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary><c>GET /api/v2/watch/{id}</c> against a server behind the path prefix <c>/silo</c>, and the shape of its answer.</summary>
public sealed class WatchApiTests
{
    private readonly ScriptedSilo _silo = new();

    private WatchApi Api => new(_silo.Http);

    private static WatchDetail Parse(string json) => JsonSerializer.Deserialize(json, PlaybackJsonContext.Default.WatchDetail)!;

    [Fact]
    public async Task GetWatch_GetsTheItemForTheProfile()
    {
        _silo.On(HttpMethod.Get, "api/v2/watch/movie%3Aheat-1995", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.WatchStateOk));

        var detail = await Api.GetWatchAsync(ScriptedSilo.Base, "movie:heat-1995", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Heat", detail.Title);
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.Equal("/silo/api/v2/watch/movie%3Aheat-1995", sent.Uri.PathAndQuery);
    }

    [Fact]
    public async Task GetWatch_SendsThePreferredFileAndTheImageSizes()
    {
        var images = ImageSizes.Default.For(JsonSerializer.Deserialize(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities));
        _silo.On(HttpMethod.Get, "api/v2/watch/episode-1", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.WatchEpisodeTwoParts));

        await Api.GetWatchAsync(ScriptedSilo.Base, "episode-1", "7", images, TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/watch/episode-1?file_id=7&poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185",
            Assert.Single(_silo.Sent).Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    public async Task GetWatch_RefusesIdsThatAreNotOne(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Api.GetWatchAsync(ScriptedSilo.Base, id, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetWatch_ASeriesIsAValidationProblem()
    {
        _silo.On(HttpMethod.Get, "api/v2/watch/series-1",
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(PlaybackPayloads.WatchStateNotPlayable, System.Text.Encoding.UTF8, "application/problem+json"),
            }));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.GetWatchAsync(ScriptedSilo.Base, "series-1", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ProblemCodes.ValidationFailed, error.Code);
        Assert.Equal("path.id", Assert.Single(error.Problem.Errors!).Location);
    }

    [Fact]
    public void Fixture_ReadsTheVersionResumeAndMarkers()
    {
        var detail = Parse(PlaybackPayloads.WatchStateOk);

        Assert.Equal("movie:heat-1995", detail.ContentId);
        Assert.Equal(WatchItemTypes.Movie, detail.Type);
        Assert.Equal(1995, detail.Year);
        Assert.Equal("/api/images/poster/heat.jpg?size=medium", detail.PosterUrl);
        Assert.Equal("/api/images/backdrop/heat.jpg?size=medium", detail.BackdropUrl);
        Assert.Null(detail.SeriesId);

        var version = Assert.Single(detail.Versions);
        Assert.Equal("42", version.FileId);
        Assert.Equal("1080p", version.Resolution);
        Assert.Equal("h264", version.CodecVideo);
        Assert.Equal("eac3", version.CodecAudio);
        Assert.Equal("mkv", version.Container);
        Assert.False(version.Hdr);
        Assert.Equal(1024, version.FileSize);
        Assert.Equal(10200, version.DurationSeconds);
        Assert.Equal(8000000, version.Bitrate);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), version.AddedAt);
        Assert.Equal(1920, Assert.Single(version.VideoTracks).Width);
        var audio = Assert.Single(version.AudioTracks);
        Assert.Equal(("eng", "eac3", 6, true), (audio.Language, audio.Codec, audio.Channels, audio.Default));
        Assert.Empty(version.SubtitleTracks);
        Assert.Equal(new WatchChapter { Index = 1, Title = "Opening", StartSeconds = 0, EndSeconds = 300, Source = "embedded" }, Assert.Single(version.Chapters));
        Assert.Equal(new WatchMarker(0, 90), version.Intro);
        var segment = Assert.Single(version.MarkerSegments);
        Assert.Equal((WatchMarkerKinds.Intro, 0d, 90d, 0d), (segment.Kind, segment.StartSeconds, segment.EndSeconds, segment.ThumbnailCaptureSeconds));
        Assert.Equal("https://images.example.test/intro.webp", segment.ThumbnailUrl);
        Assert.False(version.TrickplayAvailable);
        Assert.False(version.IsMultiPart);
        Assert.Null(version.EffectiveAudioTrackIndex);

        Assert.Equal(new WatchMarker(10000, 10200), detail.Credits);
        Assert.Equal("eng", Assert.Single(detail.Subtitles).Language);
        var user = detail.UserData!;
        Assert.Equal((1325.5, 10200d, true, false, "3"), (user.PositionSeconds, user.DurationSeconds, user.IsInProgress, user.Played, user.LastFileId));

        var variant = Assert.Single(detail.PlaybackVariants);
        Assert.Equal(("v1", 1, "42"), (variant.VariantId, variant.PartCount, variant.DefaultFileId));
        Assert.Empty(Assert.Single(variant.Parts).Versions);
    }

    [Fact]
    public void Fixture_PreferenceDefaultsApplyToOmittedMembers()
    {
        var detail = Parse(PlaybackPayloads.WatchStateOk);

        Assert.Equal("eng", detail.EffectiveSubtitleLanguage);
        // Omitted members are the contract defaults, not "unknown" (docs/api-contract.md, 8.8.1).
        Assert.Null(detail.EffectiveSubtitleMode);
        Assert.Equal(SubtitlePreferenceModes.Auto, detail.SubtitleMode);
        Assert.Null(detail.EffectiveShowForcedSubtitles);
        Assert.True(detail.ShowForcedSubtitles);
        Assert.Null(detail.EffectiveSubtitleTrackSignature);
    }

    [Fact]
    public void Episode_ReadsSeriesContextPreferencesAndParts()
    {
        var detail = Parse(PlaybackPayloads.WatchEpisodeTwoParts);

        Assert.Equal(WatchItemTypes.Episode, detail.Type);
        Assert.Equal("The You You Are", detail.Title);
        Assert.Equal(("series-tvdb-296762", "Severance", 1, 5), (detail.SeriesId, detail.SeriesTitle, detail.SeasonNumber, detail.EpisodeNumber));
        Assert.Null(detail.Year);
        Assert.Null(detail.UserData);
        Assert.Empty(detail.Subtitles);

        Assert.Equal(SubtitlePreferenceModes.Always, detail.SubtitleMode);
        Assert.False(detail.ShowForcedSubtitles);
        Assert.Equal(new WatchSubtitleSignature { Source = "external", Language = "de", Codec = "srt", Label = "Deutsch", Forced = true, HearingImpaired = true },
            detail.EffectiveSubtitleTrackSignature);
        Assert.Equal(("2160p", true, "hevc", "extended"),
            (detail.EffectiveVersionResolution, detail.EffectiveVersionHdr, detail.EffectiveVersionCodecVideo, detail.EffectiveVersionEditionKey));

        var version = Assert.Single(detail.Versions);
        Assert.Equal(40000000000, version.FileSize);
        Assert.Equal((1, "de"), (version.EffectiveAudioTrackIndex, version.EffectiveAudioLanguage));
        Assert.Equal(("extended", "Extended Cut"), (version.EditionKey, version.EditionRaw));
        Assert.Equal(("multi_part", "g1", 0, 2), (version.PresentationKind, version.PresentationGroupKey, version.PresentationPartIndex, version.PresentationPartTotal));
        Assert.True(version.IsMultiPart);
        Assert.True(version.TrickplayAvailable);
        Assert.Equal(["recap", "intro", "intro"], version.MarkerSegments.Select(m => m.Kind));

        var video = Assert.Single(version.VideoTracks);
        Assert.Equal((8, 6, 1, true, true, "none"), (video.DvProfile, video.DvLevel, video.DvBlCompatId, video.DvConfigPresent, video.DvBlCompatIdPresent, video.DvEnhancementLayer));
        Assert.True(video.Hdr10Plus);
        Assert.Equal(("main 10", 153, 10, "24000/1001", "smpte2084"), (video.Profile, video.Level, video.BitDepth, video.FrameRate, video.ColorTransfer));

        Assert.Equal(["en", "de"], version.AudioTracks.Select(a => a.Language));
        Assert.Equal("7.1", version.AudioTracks[0].Layout);

        var embedded = version.SubtitleTracks[0];
        Assert.Equal((3, "subrip", false, true), (embedded.Index, embedded.Codec, embedded.External, embedded.Default));
        var external = version.SubtitleTracks[1];
        Assert.Equal((0, true, true, true, "S01E05.de.forced.srt"), (external.Index, external.Forced, external.HearingImpaired, external.External, external.FileName));

        var variant = Assert.Single(detail.PlaybackVariants);
        Assert.Equal((2, 3000L, "extended"), (variant.PartCount, variant.TotalDurationSeconds, variant.EditionKey));
        Assert.Equal([0, 1], variant.Parts.Select(p => p.PartIndex));
        Assert.Equal(["7", "8"], variant.Parts.Select(p => p.DefaultFileId));
        Assert.Equal(1, variant.Parts[1].Versions[0].PresentationPartIndex);
    }

    [Fact]
    public void FindVersion_LooksInTheVersionsAndInEveryPart()
    {
        var detail = Parse(PlaybackPayloads.WatchEpisodeTwoParts);

        Assert.Same(detail.Versions[0], detail.FindVersion("7"));
        Assert.Equal(1, detail.FindVersion("8")!.PresentationPartIndex);
        Assert.Null(detail.FindVersion("99"));
    }

    [Fact]
    public void MissingMembers_ReadAsEmptyNotNull()
    {
        var detail = Parse(PlaybackPayloads.WatchMinimal);

        var version = Assert.Single(detail.Versions);
        Assert.Equal("1", version.FileId);
        Assert.Equal("", version.Resolution);
        Assert.Equal("", version.Container);
        Assert.Empty(version.AudioTracks);
        Assert.Empty(version.SubtitleTracks);
        Assert.Empty(version.VideoTracks);
        Assert.Empty(version.Chapters);
        Assert.Empty(version.MarkerSegments);
        Assert.Null(version.AddedAt);
        Assert.Empty(detail.PlaybackVariants);
        Assert.Null(detail.UserData);
        Assert.Null(detail.PosterUrl);
    }

    [Fact]
    public void ExplicitNulls_ReadAsEmpty()
    {
        var detail = Parse("""{ "content_id": null, "type": null, "title": null, "versions": null, "subtitles": null, "playback_variants": null }""");

        Assert.Equal(("", "", ""), (detail.ContentId, detail.Type, detail.Title));
        Assert.Empty(detail.Versions);
        Assert.Empty(detail.Subtitles);
        Assert.Empty(detail.PlaybackVariants);
    }
}
