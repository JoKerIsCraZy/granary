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
    public void HardwareDecoders_AreKnownNames_WithoutDuplicates()
    {
        // Whatever this machine's GPU offers (nothing on a machine without one), the answer is a clean list.
        var decoders = VideoDecoderProbe.HardwareDecoders();

        Assert.All(decoders.Codecs, c => Assert.Contains(c, new[] { "h264", "hevc", "vp9", "av1", "mpeg2video", "vc1", "mpeg4", "vp8" }));
        Assert.Equal(decoders.Codecs.Distinct().Count(), decoders.Codecs.Count);
        Assert.All(decoders.TenBitCodecs, c => Assert.Contains(c, decoders.Codecs));
    }

    private static readonly Guid HevcMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    private static readonly Guid HevcMain10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
    private static readonly Guid Vp9Profile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
    private static readonly Guid Vp9Profile2 = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");
    private static readonly Guid Av1Profile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");
    private static readonly Guid H264 = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");

    [Fact]
    public void Summarize_TellsAGpuWithTenBitHevcFromOneWithEightBitOnly()
    {
        var eightBit = VideoDecoderProbe.Summarize([H264, HevcMain]);
        var tenBit = VideoDecoderProbe.Summarize([H264, HevcMain, HevcMain10]);

        Assert.Equal(["h264", "hevc"], eightBit.Codecs);
        Assert.Empty(eightBit.TenBitCodecs);
        Assert.Equal(["h264", "hevc"], tenBit.Codecs);
        Assert.Equal(["hevc"], tenBit.TenBitCodecs);
    }

    [Fact]
    public void Summarize_CountsVp9AtTenBitOnlyWithProfile2()
    {
        var profile0 = VideoDecoderProbe.Summarize([Vp9Profile0]);
        var profile2 = VideoDecoderProbe.Summarize([Vp9Profile0, Vp9Profile2]);

        Assert.Equal(["vp9"], profile0.Codecs);
        Assert.Empty(profile0.TenBitCodecs);
        Assert.Equal(["vp9"], profile2.TenBitCodecs);
    }

    [Fact]
    public void Summarize_CountsAv1AtTenBit_AsItsMainProfileIncludesTenBit()
    {
        var decoders = VideoDecoderProbe.Summarize([Av1Profile0]);

        Assert.Equal(["av1"], decoders.Codecs);
        Assert.Equal(["av1"], decoders.TenBitCodecs);
    }

    [Fact]
    public void Summarize_IgnoresProfilesItDoesNotKnow()
    {
        var decoders = VideoDecoderProbe.Summarize([Guid.NewGuid(), H264]);

        Assert.Equal(["h264"], decoders.Codecs);
        Assert.Empty(decoders.TenBitCodecs);
    }
}
