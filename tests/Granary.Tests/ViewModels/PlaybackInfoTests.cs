using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The playback info overlay's rows, as the web player's playback-info.ts writes them.</summary>
public sealed class PlaybackInfoTests
{
    private static readonly WatchFileVersion Uhd = new()
    {
        FileId = "42",
        Resolution = "2160p",
        CodecVideo = "hevc",
        CodecAudio = "truehd",
        Container = "mkv",
        FileSize = 58L * 1024 * 1024 * 1024,
        Bitrate = 61234,
        VideoTracks = [new WatchVideoTrack { Codec = "hevc", Profile = "Main 10", Bitrate = 58000, DolbyVision = "Profile 8.1", VideoRange = "HDR10", ColorRange = "tv" }],
        AudioTracks =
        [
            new WatchAudioTrack { Codec = "truehd", Title = "TrueHD 7.1", Channels = 8, Bitrate = 4500, SampleRate = 48000, Default = true },
            new WatchAudioTrack { Codec = "eac3", EmbeddedTitle = "Commentary", Channels = 2, Bitrate = 640, SampleRate = 48000 },
        ],
    };

    private static readonly WatchFileVersion Hd = new() { FileId = "10", Resolution = "1080p", CodecVideo = "h264", Hdr = false };

    private static PlaybackPlan Plan(string delivery, string protocol, params PlaybackTransformation[] transformations) => new()
    {
        Delivery = delivery,
        Stream = new PlaybackStream { Url = "/api/v2/stream/s", Protocol = protocol },
        EffectiveRecipe = new EffectiveRecipe { VideoCodec = "h264", AudioCodec = "aac" },
        Transformations = transformations,
    };

    private static Dictionary<string, string> Rows(IReadOnlyList<PlaybackInfoSection> sections, string title) =>
        sections.Single(s => s.Title == title).Rows.ToDictionary(r => r.Label, r => r.Value);

    [Theory]
    [InlineData("video_to_hevc", "hevc", "HEVC (transcoded)")]
    [InlineData("video_to_h264", "h264", "H.264 (transcoded)")]
    [InlineData("hdr_to_sdr_tonemap", "hevc", "HEVC (transcoded)")]
    public void TheVideoCodec_SaysTranscoded_WhenTheServerEncodesTheVideo_WhicheverCodecItEncodesTo(string transformation, string codec, string shown)
    {
        var plan = new PlaybackPlan
        {
            Delivery = PlaybackDeliveries.ServerTranscodeHls,
            Stream = new PlaybackStream { Url = "/api/v2/stream/s", Protocol = StreamProtocols.Hls },
            EffectiveRecipe = new EffectiveRecipe { VideoCodec = codec, AudioCodec = "aac" },
            Transformations = [new PlaybackTransformation { Name = transformation, Executor = "server" }],
        };
        var stream = new PlaybackStreamInfo(plan, new Uri("https://silo.example/api/v2/stream/s/master.m3u8"), Uhd, Uhd, AudioOrdinal: 1);

        var sections = PlaybackInfo.Sections(stream, new PlaybackStats(1920, 1080, 0, 0, "d3d11va"), 2560, 1440);

        Assert.Equal(shown, Rows(sections, "Playback Stream Info")["Video codec"]);
    }

    [Fact]
    public void ATranscode_SaysWhatTheServerMade_AndTheFileItCameFrom()
    {
        var stream = new PlaybackStreamInfo(
            Plan(PlaybackDeliveries.ServerTranscodeHls, StreamProtocols.Hls, new PlaybackTransformation { Name = "video_to_h264", Executor = "server" }),
            new Uri("https://silo.example/api/v2/stream/s/master.m3u8"), Uhd, Uhd, AudioOrdinal: 1);

        var sections = PlaybackInfo.Sections(stream, new PlaybackStats(1920, 1080, 3, 0, "d3d11va"), 2560, 1440);

        Assert.Equal(["Player", "Video Info", "Playback Stream Info", "Current Source File"], sections.Select(s => s.Title));
        Assert.Equal(new Dictionary<string, string>
        {
            ["Player"] = "mpv",
            ["Play method"] = "Transcode",
            ["Protocol"] = "https",
            ["Stream type"] = "HLS",
        }, Rows(sections, "Player"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["Player dimensions"] = "2560x1440",
            ["Video resolution"] = "1920x1080",
            ["Decoding"] = "Hardware (d3d11va)",
            ["Dropped frames"] = "3",
            ["Decoder-dropped frames"] = "0",
        }, Rows(sections, "Video Info"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["Video codec"] = "H.264 (transcoded)",
            ["Audio codec"] = "AAC (copy)",
        }, Rows(sections, "Playback Stream Info"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["Container"] = "mkv",
            ["Size"] = "58.0 GiB",
            ["Bitrate"] = "61.2 Mbps",
            ["Video codec"] = "HEVC Main 10",
            ["Video bitrate"] = "58.0 Mbps",
            ["Video range type"] = "Dolby Vision Profile 8.1 (HDR10)",
            ["Color range"] = "Limited (tv)",
            // The track that plays, not the default one.
            ["Audio codec"] = "Commentary",
            ["Audio bitrate"] = "640 kbps",
            ["Audio channels"] = "2",
            ["Audio sample rate"] = "48,000 Hz",
        }, Rows(sections, "Current Source File"));
    }

    [Fact]
    public void ARemuxThatConvertsTheAudio_IsATranscode_AsTheSettingsGearSays()
    {
        var stream = new PlaybackStreamInfo(
            Plan(PlaybackDeliveries.ServerRemuxHls, StreamProtocols.Hls, new PlaybackTransformation { Name = "audio_to_aac", Executor = "server" }),
            new Uri("https://silo.example/api/v2/stream/s/master.m3u8"), Uhd, Uhd, AudioOrdinal: 0);

        var sections = PlaybackInfo.Sections(stream, PlaybackStats.None, 0, 0);

        Assert.Equal("Transcode", Rows(sections, "Player")["Play method"]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Video codec"] = "H.264 (copy)",
            ["Audio codec"] = "AAC (transcoded)",
        }, Rows(sections, "Playback Stream Info"));
    }

    // A transformation of the server's own that Granary does not know may encode either stream: the play method says
    // Transcode, and a codec line claims neither a copy nor a transcode unless a known encode says which.
    [Theory]
    [InlineData(new[] { "server_future_filter" }, "H.264", "AAC")]
    [InlineData(new[] { "server_future_filter", "audio_to_aac" }, "H.264", "AAC (transcoded)")]
    [InlineData(new[] { "server_future_filter", "video_to_h264" }, "H.264 (transcoded)", "AAC")]
    [InlineData(new[] { "server_dv7_to_hdr10" }, "H.264 (copy)", "AAC (copy)")]
    public void AServerTransformationThisAppDoesNotKnow_IsNeverCalledACopy(string[] transformations, string video, string audio)
    {
        var plan = Plan(PlaybackDeliveries.ServerRemuxHls, StreamProtocols.Hls,
            [.. transformations.Select(name => new PlaybackTransformation { Name = name, Executor = "server" })]);
        var stream = new PlaybackStreamInfo(plan, new Uri("https://silo.example/api/v2/stream/s/master.m3u8"), Uhd, Uhd, AudioOrdinal: 0);

        var sections = PlaybackInfo.Sections(stream, PlaybackStats.None, 0, 0);

        Assert.Equal(new Dictionary<string, string> { ["Video codec"] = video, ["Audio codec"] = audio }, Rows(sections, "Playback Stream Info"));
    }

    [Theory]
    [InlineData(PlaybackDeliveries.ServerRemuxProgressive, "Direct Streaming")]
    [InlineData(PlaybackDeliveries.ServerRemuxHls, "Direct Streaming")]
    [InlineData("server_future_delivery", "—")]
    public void ThePlayMethod_OfAPlanThatEncodesNothing(string delivery, string method)
    {
        var stream = new PlaybackStreamInfo(Plan(delivery, StreamProtocols.Hls), new Uri("https://silo.example/api/v2/stream/s"), Uhd, Uhd, AudioOrdinal: 0);

        Assert.Equal(method, Rows(PlaybackInfo.Sections(stream, PlaybackStats.None, 0, 0), "Player")["Play method"]);
    }

    // playback-info.test.ts, "names Atmos for a source track whose title is the codec's long name": the profile tells
    // Atmos, and "Atmospheric" in a title is not the word.
    [Theory]
    [InlineData("ATSC A/52B (AC-3, E-AC-3)", "Dolby Digital Plus + Dolby Atmos", "DD+ Atmos")]
    [InlineData("ATSC A/52B (AC-3, E-AC-3)", null, "ATSC A/52B (AC-3, E-AC-3)")]
    [InlineData("Atmospheric score", "Dolby Digital Plus + Dolby Atmos", "DD+ Atmos")]
    [InlineData("EAC3 Dolby Digital Plus + Dolby Atmos", null, "EAC3 Dolby Digital Plus + Dolby Atmos")]
    [InlineData(null, "Dolby Digital Plus + Dolby Atmos", "DD+ Atmos")]
    [InlineData(null, null, "EAC3")]
    public void TheSourceFilesAudio_NamesAtmos_WhenItsTitleDoesNot(string? title, string? profile, string shown)
    {
        var file = Hd with
        {
            CodecAudio = "eac3",
            AudioTracks = [new WatchAudioTrack { Codec = "eac3", Title = title, Profile = profile, Channels = 6, Default = true }],
        };
        var stream = new PlaybackStreamInfo(Plan(PlaybackDeliveries.OriginalHttp, StreamProtocols.HttpProgressive),
            new Uri("http://silo.local/api/v2/stream/s"), file, file, AudioOrdinal: 0);

        var sections = PlaybackInfo.Sections(stream, PlaybackStats.None, 0, 0);

        Assert.Equal(shown, Rows(sections, "Current Source File")["Audio codec"]);
    }

    [Fact]
    public void ADirectPlayAfterAnEditionFallback_SaysWhichFileWasAskedFor()
    {
        var stream = new PlaybackStreamInfo(Plan(PlaybackDeliveries.OriginalHttp, StreamProtocols.HttpProgressive),
            new Uri("http://silo.local/api/v2/stream/s"), Hd, Uhd, AudioOrdinal: null);

        var sections = PlaybackInfo.Sections(stream, PlaybackStats.None, 0, 0);

        var player = Rows(sections, "Player");
        Assert.Equal("Direct Play", player["Play method"]);
        Assert.Equal("Progressive", player["Stream type"]);
        Assert.Equal("2160p HEVC DV", player["Auto-switched from"]);
        Assert.Equal("H.264 (direct)", Rows(sections, "Playback Stream Info")["Video codec"]);
        var video = Rows(sections, "Video Info");
        Assert.Equal("—", video["Video resolution"]);
        Assert.Equal("—", video["Dropped frames"]);
        Assert.Equal("SDR", Rows(sections, "Current Source File")["Video range type"]);
    }

    [Fact]
    public void AFilePlayedDirectly_HasThePlayerSectionsOnly()
    {
        var sections = PlaybackInfo.Sections(null, new PlaybackStats(1280, 720, 0, 0, "no"), 1280, 720);

        Assert.Equal(["Player", "Video Info"], sections.Select(s => s.Title));
        Assert.Equal("Software", Rows(sections, "Video Info")["Decoding"]);
    }

    [Theory]
    [InlineData(0L, "—")]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KiB")]
    [InlineData(5L * 1024 * 1024, "5.0 MiB")]
    public void FileSizes_AreInBinaryUnits(long bytes, string expected) => Assert.Equal(expected, PlaybackInfo.FileSize(bytes));

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("vp9", "VP9")]
    [InlineData(" ", "—")]
    public void Codecs_AreNamedAsOnTheWeb(string codec, string expected) => Assert.Equal(expected, PlaybackInfo.CodecLabel(codec));

    [Fact]
    public void ADownloadPlayedFromThisPC_SaysItIsALocalFile_AndShowsTheFileAsItsManifestDescribesIt()
    {
        var file = new PlaybackLocalFile("mkv", 8L << 30, "hevc", Hdr: true, "eac3", 6);

        var sections = PlaybackInfo.Sections(null, new PlaybackStats(3840, 2160, 0, 0, "d3d11va"), 2560, 1440, file);

        Assert.Equal(new Dictionary<string, string> { ["Player"] = "mpv", ["Play method"] = "Local file", ["Protocol"] = "file" }, Rows(sections, "Player"));
        Assert.Equal("3840x2160", Rows(sections, "Video Info")["Video resolution"]);
        // No server stream: nothing was planned, remuxed or transcoded.
        Assert.DoesNotContain(sections, s => s.Title == "Playback Stream Info");
        Assert.Equal(new Dictionary<string, string>
        {
            ["Container"] = "mkv",
            ["Size"] = "8.0 GiB",
            ["Video codec"] = "HEVC",
            ["Video range type"] = "HDR",
            ["Audio codec"] = "EAC3",
            ["Audio channels"] = "6",
        }, Rows(sections, "Current Source File"));
    }

    [Fact]
    public void WhatADownloadsManifestDoesNotSay_ReadsAsNotKnown()
    {
        var sections = PlaybackInfo.Sections(null, PlaybackStats.None, 0, 0, new PlaybackLocalFile(null, 0, null, Hdr: false, null, 0));

        Assert.Equal(new Dictionary<string, string>
        {
            ["Container"] = "—",
            ["Size"] = "—",
            ["Video codec"] = "—",
            ["Video range type"] = "SDR",
            ["Audio codec"] = "—",
            ["Audio channels"] = "—",
        }, Rows(sections, "Current Source File"));
    }

    [Fact]
    public void AFilePlayedDirectly_HasNeitherAPlayMethodNorASourceFile()
    {
        var sections = PlaybackInfo.Sections(null, PlaybackStats.None, 0, 0);

        Assert.Equal(new Dictionary<string, string> { ["Player"] = "mpv" }, Rows(sections, "Player"));
        Assert.DoesNotContain(sections, s => s.Title == "Current Source File");
    }
}
