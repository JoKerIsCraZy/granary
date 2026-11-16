using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>A stream of Activity as its row shows it, and how a read again changes the row it already has.</summary>
public sealed class AdminSessionRowTests
{
    private static readonly Uri Server = new("https://silo.example");

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private static readonly AdminPlaybackSession Direct = new()
    {
        SessionId = "s1", PlayMethod = "direct", SourceVideoCodec = "hevc", SourceVideoResolution = "2160p", SourceAudioCodec = "truehd",
        SourceAudioChannels = 8, StartedAt = Now.AddMinutes(-5),
    };

    private static readonly AdminPlaybackSession Transcode = Direct with
    {
        PlayMethod = "transcode", VideoDecision = "transcode", AudioDecision = "transcode", TargetVideoCodec = "h264",
        TargetResolution = "1080p", TargetAudioCodec = "aac", TargetAudioChannels = 6, TranscodeHwAccel = "qsv",
    };

    [Fact]
    public void AStreamPlayingDirectly_ShowsItsSourceFormats_Muted()
    {
        var row = new AdminSessionRow(Direct, Server, Now);

        Assert.Equal(("HEVC 2160p", false, "", false), (row.VideoLine, row.IsVideoConverted, row.VideoEncoder, row.HasVideoEncoder));
        Assert.Equal(("TrueHD 7.1", false), (row.AudioLine, row.IsAudioConverted));
    }

    [Fact]
    public void AStreamThatStartsToTranscode_ShowsItsConversions_OnTheSameRow()
    {
        var row = new AdminSessionRow(Direct, Server, Now);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Update(Transcode, Now.AddSeconds(10));

        Assert.Equal(("HEVC 2160p → H.264 1080p", true, "HW QSV", true), (row.VideoLine, row.IsVideoConverted, row.VideoEncoder, row.HasVideoEncoder));
        Assert.Equal(("TrueHD 7.1 → AAC 5.1", true), (row.AudioLine, row.IsAudioConverted));
        // The row stays while its session plays: the view hears of each line that changed.
        foreach (string name in new[]
        {
            nameof(row.VideoLine), nameof(row.IsVideoConverted), nameof(row.VideoEncoder), nameof(row.HasVideoEncoder), nameof(row.AudioLine),
            nameof(row.IsAudioConverted),
        })
        {
            Assert.Contains(name, changed);
        }
    }
}
