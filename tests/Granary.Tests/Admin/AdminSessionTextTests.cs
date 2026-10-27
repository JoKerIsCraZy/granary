using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>What Activity says reaches a viewer, as the web's adminActivityPresentation tests have it.</summary>
public sealed class AdminSessionTextTests
{
    private static readonly AdminPlaybackSession Direct = new()
    {
        PlayMethod = "direct", SourceContainer = "mkv", SourceVideoCodec = "hevc", SourceVideoResolution = "2160p", SourceAudioCodec = "truehd",
        SourceAudioChannels = 8, SourceAudioLanguage = "eng", OutputContainer = "mkv", OutputProtocol = "http",
    };

    private static readonly AdminPlaybackSession Transcode = Direct with
    {
        PlayMethod = "transcode", VideoDecision = "transcode", AudioDecision = "transcode", OutputContainer = "fmp4", OutputProtocol = "hls",
        TargetVideoCodec = "h264", TargetResolution = "1080p", TargetAudioCodec = "aac", TargetAudioChannels = 6, TranscodeHwAccel = "qsv",
    };

    [Fact]
    public void DirectPlay_DeliversTheSource()
    {
        Assert.Equal("MKV", AdminSessionText.Container(Direct));
        Assert.Equal("HEVC · 2160p", AdminSessionText.Video(Direct));
        Assert.Equal("eng · TrueHD 7.1", AdminSessionText.Audio(Direct));
        Assert.Equal("", AdminSessionText.TranscodeMode(Direct));
    }

    [Fact]
    public void Transcode_DeliversTheTarget()
    {
        Assert.Equal("fMP4 (HLS)", AdminSessionText.Container(Transcode));
        Assert.Equal("H.264 · 1080p", AdminSessionText.Video(Transcode));
        // The source's 7.1 was downmixed: the target's layout, never the source's.
        Assert.Equal("AAC 5.1", AdminSessionText.Audio(Transcode));
        Assert.Equal("AAC", AdminSessionText.Audio(Transcode with { TargetAudioChannels = null }));
        Assert.Equal("HW QSV", AdminSessionText.TranscodeMode(Transcode));
    }

    [Fact]
    public void FullResolutionTranscode_IsNamedAsTheSource()
    {
        var session = Transcode with { SourceVideoResolution = "2160p", SourceVideoHeight = 1600, TargetResolution = "1600p" };

        Assert.Equal("H.264 · 2160p", AdminSessionText.Video(session));
        Assert.Equal("H.264 · 1080p", AdminSessionText.Video(session with { TargetResolution = "1080p" }));
    }

    [Theory]
    [InlineData("mpegts", "hls", "MPEG-TS (HLS)")]
    [InlineData("mp4", "http", "MP4")]
    [InlineData(null, "hls", "Unknown output container (HLS)")]
    [InlineData(null, null, "Unknown output container")]
    public void Remux_NamesItsOutputContainer(string? container, string? protocol, string expected) =>
        Assert.Equal(expected, AdminSessionText.Container(new AdminPlaybackSession { PlayMethod = "remux", OutputContainer = container, OutputProtocol = protocol }));

    [Theory]
    [InlineData("vaapi", "HW VAAPI")]
    [InlineData("none", "SW")]
    [InlineData("auto", "HW/SW pending")]
    [InlineData(null, "HW/SW unknown")]
    [InlineData("nvenc", "HW NVENC")]
    public void TranscodeMode_NamesTheEncoder(string? accel, string expected) =>
        Assert.Equal(expected, AdminSessionText.TranscodeMode(Transcode with { TranscodeHwAccel = accel }));

    [Fact]
    public void AudioOnlyTranscode_SaysSo()
    {
        var session = Direct with { PlayMethod = "remux", VideoDecision = "copy", TranscodeAudio = true };

        Assert.Equal("Audio Transcode", AdminSessionText.TranscodeMode(session));
        Assert.Equal("AAC", AdminSessionText.Audio(session));
    }

    [Fact]
    public void UnknownSource_SaysSo()
    {
        var session = new AdminPlaybackSession { PlayMethod = "direct" };

        Assert.Equal("Unknown source", AdminSessionText.Container(session));
        Assert.Equal("Unknown source", AdminSessionText.Video(session));
        Assert.Equal("Unknown source", AdminSessionText.Audio(session));
        Assert.Equal("Transcoding", AdminSessionText.Video(session with { PlayMethod = "transcode" }));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "")]
    [InlineData(640, "640 kbps")]
    [InlineData(8000, "8.0 Mbps")]
    [InlineData(12_345, "12.3 Mbps")]
    public void Bitrate_InKbpsOrMbps(int? kbps, string expected) => Assert.Equal(expected, AdminSessionText.Bitrate(kbps));

    [Fact]
    public void Position_AndProgress_FollowTheFilesLength()
    {
        var session = new AdminPlaybackSession { PositionSeconds = 724.9, FileDuration = 2910 };

        Assert.Equal("12:04 / 48:30", AdminSessionText.Position(session));
        Assert.Equal(724.9 / 2910, AdminSessionText.Progress(session), 6);
        Assert.Equal("12:04", AdminSessionText.Position(session with { FileDuration = null }));
        Assert.Equal(0, AdminSessionText.Progress(session with { FileDuration = 0 }));
        Assert.Equal("1:02:09", AdminSessionText.Clock(3729));
        Assert.Equal("0:00", AdminSessionText.Clock(double.NaN));
    }

    [Fact]
    public void Subtitle_NamesTheEpisodeAndSeries()
    {
        Assert.Equal("S1E3 · Dark", AdminSessionText.Subtitle(new AdminPlaybackSession { SeriesName = "Dark", SeasonNumber = 1, EpisodeNumber = 3 }));
        Assert.Equal("Movie", AdminSessionText.Subtitle(new AdminPlaybackSession { MediaType = "movie" }));
        Assert.Equal("", AdminSessionText.Subtitle(new AdminPlaybackSession { MediaType = "episode" }));
    }

    [Fact]
    public void Route_OfAnOlderServer_IsItsReportingNode()
    {
        Assert.Equal([new AdminRouteNode("legacy:Local server", "Node", "Local server")],
            AdminSessionText.Route(new AdminPlaybackSession { ReportingNode = "local" }));
        Assert.Equal([new AdminRouteNode("legacy:gpu-box", "Node", "gpu-box")],
            AdminSessionText.Route(new AdminPlaybackSession { ReportingNode = "local", NodeDisplayName = "gpu-box" }));
    }

    [Fact]
    public void Route_RunsFromTheWorkToTheViewer()
    {
        var session = new AdminPlaybackSession
        {
            RoutingWorkload = "transcode", RoutingExecution = "transcode", RoutingExecutionNodeName = "gpu", RoutingEgress = "proxy",
            RoutingEgressNodeName = "edge", ReportingNode = "local",
        };

        Assert.Equal([new AdminRouteNode("transcode:gpu", "Transcode", "gpu"), new AdminRouteNode("proxy:edge", "Proxy", "edge")],
            AdminSessionText.Route(session));
        Assert.Equal([new AdminRouteNode("transcode:gpu", "Transcode", "gpu"), new AdminRouteNode("server:local", "Server", "Local server")],
            AdminSessionText.Route(session with { RoutingEgress = "api" }));
        Assert.Equal([new AdminRouteNode("server:main", "Server", "main")],
            AdminSessionText.Route(session with { RoutingExecution = "api", RoutingEgress = "", ReportingNode = "main" }));
        // The same proxy doing the work and the egress is one node.
        Assert.Single(AdminSessionText.Route(session with { RoutingExecution = "proxy", RoutingExecutionNodeName = "edge" }));
    }
}
