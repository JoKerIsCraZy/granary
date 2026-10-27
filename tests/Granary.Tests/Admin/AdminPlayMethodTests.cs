using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>A session's method, as the web's <c>classifyActivityMethod</c> reduces it.</summary>
public sealed class AdminPlayMethodTests
{
    [Fact]
    public void ServersOwnMethod_Wins()
    {
        var session = new AdminPlaybackSession { EffectivePlayMethod = "direct_stream", VideoDecision = "transcode" };

        Assert.Equal(AdminPlayMethod.DirectStream, AdminPlayMethod.Of(session));
    }

    [Theory]
    [InlineData("transcode", null, false, "", "transcode")]
    [InlineData("copy", "transcode", false, "", "direct_stream")]
    [InlineData("copy", null, true, "", "direct_stream")]
    [InlineData("direct", "direct", false, "", "direct")]
    [InlineData(null, null, false, "direct", "direct")]
    [InlineData("copy", "copy", false, "", "remux")]
    [InlineData(null, null, false, "remux", "remux")]
    [InlineData("direct", "copy", false, "", "remux")]
    [InlineData(null, null, false, "transcode", "transcode")]
    // Nothing known of the video: a bare audio flag does not make it a direct stream.
    [InlineData(null, null, true, "", "unknown")]
    [InlineData(null, null, false, "", "unknown")]
    public void WithoutIt_TheDecisionsDecide(string? video, string? audio, bool transcodeAudio, string playMethod, string expected)
    {
        var session = new AdminPlaybackSession { VideoDecision = video, AudioDecision = audio, TranscodeAudio = transcodeAudio, PlayMethod = playMethod };

        Assert.Equal(expected, AdminPlayMethod.Of(session));
    }

    [Theory]
    [InlineData("direct", "Direct Play")]
    [InlineData("remux", "Remux")]
    [InlineData("direct_stream", "Direct Stream")]
    [InlineData("transcode", "Transcode")]
    [InlineData("hls", "Unknown")]
    public void Label_IsTheWebsWord(string method, string expected) => Assert.Equal(expected, AdminPlayMethod.Label(method));
}
