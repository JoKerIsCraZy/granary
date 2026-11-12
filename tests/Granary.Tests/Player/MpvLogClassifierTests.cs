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
    // An apostrophe in a folder's name does not end the path.
    [InlineData("Cannot open file 'D:\\Elena's films\\dl_1\\media.mkv': No such file or directory", "Cannot open file '…\\media.mkv': No such file or directory")]
    // A folder alone goes whole: its last part can be the Windows user's name.
    [InlineData("Watching directory C:\\Users\\elena", "Watching directory …")]
    [InlineData("Config dir: \"C:\\Users\\Elena Moreau\\AppData\\Roaming\\mpv\"", "Config dir: \"…\"")]
    // So does a folder with a dot in its name: only a name of a file the player reads is kept.
    [InlineData("Watching directory C:\\Users\\elena.moreau", "Watching directory …")]
    [InlineData("Watching directory \\\\nas\\home\\e.moreau", "Watching directory …")]
    [InlineData("Reading config file C:\\Users\\elena.moreau\\AppData\\Local\\Granary\\mpv\\mpv.conf", "Reading config file …\\mpv.conf")]
    // What follows a folder on the line can end like a file: the folder's name is not taken for the start of one.
    [InlineData("Cannot open C:\\Users\\elena fail a.mkv", "Cannot open … a.mkv")]
    [InlineData("Cannot open C:\\Users\\elena.moreau, fallback x.mkv", "Cannot open … x.mkv")]
    // So a file's name with spaces keeps its last word, which still says what kind of file it is.
    [InlineData("Playing: D:\\Films\\Elena Moreau.mkv", "Playing: … Moreau.mkv")]
    // What follows a file on the line stays as it is.
    [InlineData("Failed to load C:\\Users\\elena\\dl_1\\media.mkv: see mpv.conf", "Failed to load …\\media.mkv: see mpv.conf")]
    // A share written with forward slashes, bare or as a file URL.
    [InlineData("Failed to open //nas/home/elena/dl_1/media.mkv.", "Failed to open …/media.mkv.")]
    [InlineData("Opening file://nas/home/elena/dl_1/media.mkv", "Opening …/media.mkv")]
    [InlineData("Watching directory //nas/home/e.moreau", "Watching directory …")]
    [InlineData("Opening https://silo.example/api/v2/stream/a.mkv", "Opening https://silo.example/api/v2/stream/a.mkv")]
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
