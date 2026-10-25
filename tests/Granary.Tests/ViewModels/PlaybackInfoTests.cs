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
}
