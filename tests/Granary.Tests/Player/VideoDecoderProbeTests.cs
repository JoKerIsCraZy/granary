using Granary.Player.Interop;

namespace Granary.Tests.Player;

public sealed class VideoDecoderProbeTests
{
    [Theory]
    [InlineData("1b81be68-a0c7-11d3-b984-00c04f2e73c5", "h264")]
    [InlineData("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0", "hevc")]
    [InlineData("107af0e0-ef1a-4d19-aba8-67a163073d13", "hevc")]
    [InlineData("463707f8-a1d0-4585-876d-83aa6d60b89e", "vp9")]
    [InlineData("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a", "av1")]
    [InlineData("ee27417f-5e28-4e65-beea-1d26b508adc9", "mpeg2video")]
    [InlineData("1b81bea3-a0c7-11d3-b984-00c04f2e73c5", "vc1")]
    public void Codec_NamesADecoderProfileTheWayFfprobeDoes(string profile, string codec)
    {
        Assert.Equal(codec, VideoDecoderProbe.Codec(new Guid(profile)));
    }

    [Fact]
    public void Codec_IgnoresProfilesItDoesNotKnow()
    {
        Assert.Null(VideoDecoderProbe.Codec(Guid.NewGuid()));
    }

    [Fact]
    public void HardwareCodecs_AreKnownNames_WithoutDuplicates()
    {
        // Whatever this machine's GPU offers (nothing on a machine without one), the answer is a clean list.
        var codecs = VideoDecoderProbe.HardwareCodecs();

        Assert.All(codecs, c => Assert.Contains(c, new[] { "h264", "hevc", "vp9", "av1", "mpeg2video", "vc1", "mpeg4", "vp8" }));
        Assert.Equal(codecs.Distinct().Count(), codecs.Count);
    }
}
