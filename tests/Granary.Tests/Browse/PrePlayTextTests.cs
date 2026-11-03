using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Browse;

/// <summary>The pickers' words as Silo's web detail page writes them (VersionFlyout.tsx, versionFormatUtils.ts, prePlaySelection.ts).</summary>
public sealed class PrePlayTextTests
{
    [Fact]
    public void AFilesQuality_IsItsResolutionCodecRangeAndAudio()
    {
        var remux = new FileVersion
        {
            FileId = "1",
            Resolution = "2160p",
            CodecVideo = "hevc",
            CodecAudio = "truehd",
            Hdr = true,
            VideoTracks = [new WatchVideoTrack { Codec = "hevc", DolbyVision = "Profile 8.1", VideoRangeType = "DOVIWithHDR10" }],
            // The first track's titles tell Atmos when its codec is the file's.
            AudioTracks = [new WatchAudioTrack { Codec = "truehd", Title = "TrueHD Atmos 7.1" }],
        };

        Assert.Equal("2160p · HEVC · DV HDR10 · TrueHD Atmos", PrePlayText.QualitySummary(remux));
        Assert.Equal("1080p · H264 · HDR", PrePlayText.QualitySummary(new FileVersion { Resolution = "1080p", CodecVideo = "h264", Hdr = true }));
        Assert.Equal("MKV", PrePlayText.QualitySummary(new FileVersion { Container = "mkv" }));
    }

    [Theory]
    [InlineData("Night.Harbor.2025.2160p.UHD.BluRay.REMUX.HDR10.mkv", "Remux")]
    [InlineData("Night Harbor (2025) WEB-DL 1080p.mkv", "WEB-DL")]
    [InlineData("night.harbor.webrip.mkv", "WEBRip")]
    [InlineData("Night.Harbor.BluRay.x264.mkv", "BluRay")]
    [InlineData("Night.Harbor.HDTV.mkv", "HDTV")]
    [InlineData("Night.Harbor.mkv", null)]
    [InlineData(null, null)]
    public void AReleaseName_TellsItsSource(string? fileName, string? expected) => Assert.Equal(expected, PrePlayText.SourceHint(fileName));

    [Theory]
    [InlineData(41_768_556_954L, "38.9 GB")]
    [InlineData(9_234_179_686L, "8.6 GB")]
    [InlineData(734_003_200L, "700.0 MB")]
    [InlineData(2_048L, "2.0 KB")]
    [InlineData(500L, "500 B")]
    [InlineData(0L, "")]
    public void Sizes_HaveOneDecimal(long bytes, string expected) => Assert.Equal(expected, PrePlayText.FileSize(bytes));

    [Fact]
    public void Files_GoFromTheHighestResolution_KeepingTheServersOrderOtherwise()
    {
        FileVersion[] files =
        [
            new() { FileId = "a", Resolution = "720p" },
            new() { FileId = "b", Resolution = "4K" },
            new() { FileId = "c", Resolution = "" },
            new() { FileId = "d", Resolution = "720p" },
            new() { FileId = "e", Resolution = "2160p" },
        ];

        Assert.Equal(["b", "e", "a", "d", "c"], PrePlayText.SortByResolution(files).Select(f => f.FileId));
    }

    [Fact]
    public void AFileWithoutFacts_IsNamedByItsSizeOrNumber()
    {
        Assert.Equal("1.0 GB", PrePlayText.VersionSummary(new FileVersion { FileId = "7", FileSize = 1L << 30 }));
        Assert.Equal("Version 7", PrePlayText.VersionSummary(new FileVersion { FileId = "7" }));
    }

    [Fact]
    public void AnAudioTrack_IsItsLanguageCodecAndChannels()
    {
        Assert.Equal("German · DD+ Atmos · 7.1", PrePlayText.AudioSummary(new WatchAudioTrack { Language = "ger", Codec = "eac3", Profile = "Dolby Digital Plus + Dolby Atmos", Channels = 8 }));
        Assert.Equal("Unknown · AAC", PrePlayText.AudioSummary(new WatchAudioTrack { Codec = "aac" }));
        Assert.Equal("Unknown", PrePlayText.AudioSummary(null));
    }

    [Theory]
    [InlineData("English", null, "subrip", false, true, "English (SDH) · SRT")]
    [InlineData("English", null, "hdmv_pgs_subtitle", true, false, "English (Forced) · PGS")]
    [InlineData("", "Commentary", "mov_text", false, false, "Commentary · MOV_TEXT")]
    [InlineData("", null, null, false, false, "Unknown")]
    public void ASubtitle_IsItsLanguageFlagsAndFormat(string language, string? label, string? codec, bool forced, bool hearingImpaired, string expected) =>
        Assert.Equal(expected, PrePlayText.SubtitleSummary(language, label, codec, forced, hearingImpaired));
}
