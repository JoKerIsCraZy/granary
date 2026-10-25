using Granary.Player;

namespace Granary.Tests.Player;

public sealed class MpvLogClassifierTests
{
    [Theory]
    [InlineData("Playing: https://nas.example/silo/api/v2/stream/abc?file_id=4&sig=xyz",
        "Playing: https://nas.example/silo/api/v2/stream/abc?…")]
    [InlineData("Opening http://127.0.0.1:8090/api/v2/stream/abc/subtitles/2.vtt?exp=1&sig=s failed",
        "Opening http://127.0.0.1:8090/api/v2/stream/abc/subtitles/2.vtt?… failed")]
    [InlineData("Playing: C:\\Videos\\clip.mkv", "Playing: C:\\Videos\\clip.mkv")]
    public void Redact_KeepsUrlsButNotTheirQuery(string line, string expected)
    {
        // A plan's URL may carry a signed query under any name; the path is enough to diagnose.
        Assert.Equal(expected, MpvLogClassifier.Redact(line));
    }

    [Fact]
    public void Redact_DropsALineThatMentionsTheAuthorizationHeader()
    {
        Assert.Equal("<redacted: line contained an Authorization header>",
            MpvLogClassifier.Redact("http-header-fields: Authorization: Bearer abc"));
    }

    [Theory]
    [InlineData("curl", "HTTP error 401", MpvNetworkFailure.Unauthorized)]
    [InlineData("ffmpeg", "HTTP error 403 Forbidden", MpvNetworkFailure.Forbidden)]
    [InlineData("cplayer", "HTTP error 401", MpvNetworkFailure.None)]
    public void Classify_ReadsHttpErrorsFromTheStreamLayers(string prefix, string text, MpvNetworkFailure expected)
    {
        Assert.Equal(expected, MpvLogClassifier.Classify(prefix, text));
    }
}
