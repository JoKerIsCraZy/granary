using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Browse;

/// <summary>
/// The hero's quality badges, as the web's QualityBadges.tsx shows what selectedMediaSummary.ts reads from the selected
/// file: its resolution, its range label (lib/videoRange.ts) and its best audio (versionRankingUtils.ts pickBestAttributes,
/// lib/mediaFormat.ts mapAudioLabel), in capitals as the web's <c>uppercase</c> shows them.
/// </summary>
public sealed class QualityBadgesTests
{
    private const string AtmosProfile = "Dolby Digital Plus + Dolby Atmos";

    private static CatalogItemDetail Item(string type) => new() { ContentId = "item-1", Type = type, Title = "Example Movie" };

    private static IReadOnlyList<string> Badges(FileVersion version, string type = CatalogItemTypes.Movie) =>
        QualityBadges.For(Item(type), version);

    // The tester's episode: 1080p, no HDR, E-AC-3 whose profile names Atmos (mapAudioLabel: "DD+ Atmos").
    [Fact]
    public void AnSdrFileWithDolbyDigitalPlusAtmos_ShowsItsResolutionAndTheAudio()
    {
        var episode = new FileVersion
        {
            FileId = "1",
            Resolution = "1080p",
            CodecVideo = "h264",
            CodecAudio = "eac3",
            VideoTracks = [new WatchVideoTrack { Codec = "h264" }],
            AudioTracks = [new WatchAudioTrack { Codec = "eac3", Profile = AtmosProfile, Channels = 6, Default = true }],
        };

        Assert.Equal(["1080P", "DD+ ATMOS"], Badges(episode, CatalogItemTypes.Episode));
    }

    // selectedMediaSummary.test.ts, "derives the video range label from the selected file's tracks": "DV HDR10".
    [Fact]
    public void ADolbyVisionFileWithTrueHdAtmos_ShowsAllThree_InTheWebsOrder()
    {
        var remux = new FileVersion
        {
            FileId = "1",
            Resolution = "2160p",
            CodecVideo = "hevc",
            CodecAudio = "truehd",
            Hdr = true,
            VideoTracks = [new WatchVideoTrack { Codec = "hevc", DolbyVision = "Profile 8.1", VideoRangeType = "DOVIWithHDR10" }],
            AudioTracks = [new WatchAudioTrack { Codec = "truehd", Title = "TrueHD Atmos 7.1", Channels = 8 }],
        };

        Assert.Equal(["2160P", "DV HDR10", "TRUEHD ATMOS"], Badges(remux));
    }

    [Theory]
    [InlineData("HDR10", null, false, "HDR10")]
    [InlineData("HDR10Plus", null, false, "HDR10+")]
    [InlineData("HLG", null, false, "HLG")]
    [InlineData(null, "smpte2084", false, "HDR10")]
    // A file probed before the range was read has only its flag (selectedMediaSummary.test.ts: "HDR").
    [InlineData(null, null, true, "HDR")]
    public void AnHdrFile_NamesItsRange(string? rangeType, string? transfer, bool hdr, string expected)
    {
        var file = new FileVersion
        {
            FileId = "1",
            Resolution = "2160p",
            CodecAudio = "dts",
            Hdr = hdr,
            VideoTracks = [new WatchVideoTrack { Codec = "hevc", VideoRangeType = rangeType, ColorTransfer = transfer }],
        };

        Assert.Equal(["2160P", expected, "DTS"], Badges(file));
    }

    // versionRankingUtils.test.ts, "returns empty audioLabel when codec_audio is empty and no audio_tracks".
    [Fact]
    public void AFileWithoutAudioInfo_HasNoAudioBadge()
    {
        Assert.Equal(["1080P"], Badges(new FileVersion { FileId = "1", Resolution = "1080p", CodecVideo = "h264" }));
        Assert.Empty(Badges(new FileVersion { FileId = "1" }));
    }

    // selectedMediaSummary.test.ts, "only considers audio tracks from the selected file": aac beside truehd is "TrueHD".
    [Fact]
    public void TheAudioBadge_IsTheBestOfTheFilesTracks()
    {
        var file = new FileVersion
        {
            FileId = "1",
            Resolution = "1080p",
            CodecAudio = "aac",
            AudioTracks = [new WatchAudioTrack { Language = "en", Codec = "aac" }, new WatchAudioTrack { Language = "en", Codec = "truehd" }],
        };

        Assert.Equal(["1080P", "TRUEHD"], Badges(file));
    }

    // versionRankingUtils.test.ts, "labels Atmos from a later track of the same codec" and "does not read a profile from
    // a first track that is not codec_audio's stream".
    [Fact]
    public void Atmos_IsReadFromALaterTrackOfTheSameCodec_ButNotFromATrackThatCannotCarryIt()
    {
        var later = new FileVersion
        {
            FileId = "1",
            CodecAudio = "eac3",
            AudioTracks = [new WatchAudioTrack { Codec = "eac3" }, new WatchAudioTrack { Codec = "eac3", Profile = AtmosProfile }],
        };
        var other = new FileVersion
        {
            FileId = "2",
            CodecAudio = "eac3",
            AudioTracks = [new WatchAudioTrack { Codec = "aac", Profile = AtmosProfile }],
        };

        Assert.Equal(["DD+ ATMOS"], Badges(later));
        Assert.Equal(["EAC3"], Badges(other));
    }

    // The web ranks only the families of AUDIO_RANK and the classes of RESOLUTION_RANK: a file of none of them gets no
    // badge for it (bestAttributes starts both at -1 and takes only a higher score).
    [Theory]
    [InlineData("1080p", "ac3", new[] { "1080P" })]
    [InlineData("1080p", "opus", new[] { "1080P" })]
    [InlineData("4320p", "flac", new[] { "FLAC" })]
    [InlineData("4k", "dts-hd ma", new[] { "4K", "DTS-HD" })]
    public void WhatTheWebDoesNotRank_HasNoBadge(string resolution, string audio, string[] expected) =>
        Assert.Equal(expected, Badges(new FileVersion { FileId = "1", Resolution = resolution, CodecAudio = audio }));

    // Only MovieContent.tsx and EpisodeContent.tsx show them; SeriesContent.tsx and SeasonContent.tsx have the metadata
    // badges alone.
    [Theory]
    [InlineData(CatalogItemTypes.Movie, true)]
    [InlineData(CatalogItemTypes.Episode, true)]
    [InlineData(CatalogItemTypes.Series, false)]
    [InlineData(CatalogItemTypes.Season, false)]
    public void OnlyAMovieOrAnEpisode_HasThem(string type, bool shown)
    {
        var file = new FileVersion { FileId = "1", Resolution = "1080p", CodecAudio = "aac" };

        Assert.Equal(shown ? ["1080P", "AAC"] : [], Badges(file, type));
    }

    // selectedMediaSummary.test.ts, "falls back to item runtime when no selected file duration is available": no file, no badges.
    [Fact]
    public void WithoutAFile_ThereAreNone() => Assert.Empty(QualityBadges.For(Item(CatalogItemTypes.Movie), null));
}
