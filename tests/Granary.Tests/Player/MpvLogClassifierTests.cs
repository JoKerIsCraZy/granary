using Granary.Player;

namespace Granary.Tests.Player;

public sealed class MpvLogClassifierTests
{
    [Theory]
    [InlineData("Playing: https://nas.example/silo/api/v2/stream/abc?file_id=4&sig=xyz",
        "Playing: https://nas.example/silo/api/v2/stream/abc?…")]
    [InlineData("Opening http://127.0.0.1:8090/api/v2/stream/abc/subtitles/2.vtt?exp=1&sig=s failed",
        "Opening http://127.0.0.1:8090/api/v2/stream/abc/subtitles/2.vtt?… failed")]
    public void Redact_KeepsUrlsButNotTheirQuery(string line, string expected)
    {
        // A plan's URL may carry a signed query under any name; the path is enough to diagnose.
        Assert.Equal(expected, MpvLogClassifier.Redact(line));
    }

    [Theory]
    [InlineData("Playing: C:\\Users\\elena\\Videos\\clip.mkv", "Playing: …\\clip.mkv")]
    [InlineData("Playing: \\\\?\\C:\\Users\\elena\\AppData\\Local\\Granary\\downloads\\srv1\\acct-1\\7\\dl_1\\media.mkv", "Playing: …\\media.mkv")]
    [InlineData("Cannot open file 'C:\\Users\\Elena Moreau\\downloads\\dl_1\\sub-external_0.srt': No such file or directory",
        "Cannot open file '…\\sub-external_0.srt': No such file or directory")]
    [InlineData("Failed to open \\\\nas\\media\\elena\\dl_1\\media.mkv.", "Failed to open …\\media.mkv.")]
    [InlineData("Can not open external file file:///C:/Users/elena/downloads/sub.srt.", "Can not open external file file:///…/sub.srt.")]
    [InlineData("AO: [wasapi] 48000Hz stereo 2ch float", "AO: [wasapi] 48000Hz stereo 2ch float")]
    public void Redact_KeepsALocalFilesName_ButNotItsFolder(string line, string expected)
    {
        // A download lies under the Windows user's folder, which names them; the file's name is enough to diagnose.
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
