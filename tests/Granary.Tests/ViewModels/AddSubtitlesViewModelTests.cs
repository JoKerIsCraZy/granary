using System.Net;
using Granary.Core.Api.Subtitles;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Api;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The player's Add Subtitles dialog: upload with the language detected, and the online search.</summary>
public sealed class AddSubtitlesViewModelTests : IDisposable
{
    private readonly ScriptedSilo _silo = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("granary-subtitles-").FullName;
    private readonly List<StoredSubtitle> _added = [];
    private int _closes;

    public AddSubtitlesViewModelTests() =>
        _silo.On(HttpMethod.Get, "api/v2/subtitles/providers/status", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "allowed": true, "enabled": true, "providers": ["opensubtitles"], "revision": "1", "schema_version": 1, "state": "available" }"""));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AddSubtitlesViewModel Open()
    {
        var dialog = new AddSubtitlesViewModel(new SubtitlesApi(_silo.Http),
            new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" },
            new LogSink().For<AddSubtitlesViewModel>());
        dialog.Added += (_, stored) => _added.Add(stored);
        dialog.CloseRequested += (_, _) => _closes++;
        dialog.Open("42");
        return dialog;
    }

    private string File(string name, int bytes = 64)
    {
        string path = Path.Combine(_folder, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private void Detects(string language, string source) =>
        _silo.On(HttpMethod.Post, "api/v2/subtitles/detect-language",
            ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "language": "{{language}}", "source": "{{source}}" }"""));

    [Fact]
    public async Task AFile_HasItsLanguageDetected_AndIsUploadedWithIt()
    {
        Detects("de", "filename");
        _silo.On(HttpMethod.Post, "api/v2/subtitles/upload", ScriptedSilo.Json(HttpStatusCode.OK, SubtitlesApiTests.Stored));
        var dialog = Open();

        await dialog.SelectFileAsync(File("Heat.de.srt"));

        Assert.Equal("Heat.de.srt", dialog.FileName);
        Assert.Equal("de", dialog.UploadLanguage?.Value);
        Assert.Equal("Detected German from filename", dialog.DetectionText);
        Assert.True(dialog.CanUpload);

        await dialog.UploadCommand.ExecuteAsync(null);

        string body = _silo.SentTo(HttpMethod.Post, "api/v2/subtitles/upload").Single().Body!;
        Assert.Contains("name=language\r\n\r\nde\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=language_override\r\n\r\nfalse\r\n", body, StringComparison.Ordinal);
        Assert.Equal("77", Assert.Single(_added).Id);
        Assert.Equal(1, _closes);
    }

    [Fact]
    public async Task ALanguageTheViewerPicks_IsKeptByTheServer()
    {
        Detects("de", "content");
        _silo.On(HttpMethod.Post, "api/v2/subtitles/upload", ScriptedSilo.Json(HttpStatusCode.OK, SubtitlesApiTests.Stored));
        var dialog = Open();
        await dialog.SelectFileAsync(File("track.srt"));

        dialog.UploadLanguage = dialog.UploadLanguages.Single(c => c.Value == "fr");
        dialog.HearingImpaired = true;
        await dialog.UploadCommand.ExecuteAsync(null);

        Assert.Equal("", dialog.DetectionText);
        string body = _silo.SentTo(HttpMethod.Post, "api/v2/subtitles/upload").Single().Body!;
        Assert.Contains("name=language\r\n\r\nfr\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=language_override\r\n\r\ntrue\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=hearing_impaired\r\n\r\ntrue\r\n", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("movie.mkv", 64, "Unsupported file type. Use SRT, VTT, ASS, SSA, or SUB.")]
    [InlineData("huge.srt", 5 * 1024 * 1024 + 1, "The file is larger than 5 MB.")]
    public async Task AFileThatCannotBeUploaded_IsRefusedBeforeItIsSent(string name, int bytes, string error)
    {
        var dialog = Open();

        await dialog.SelectFileAsync(File(name, bytes));

        Assert.Equal(error, dialog.UploadError);
        Assert.False(dialog.HasFile);
        Assert.DoesNotContain(_silo.Sent, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ARefusedUpload_ShowsTheServersWords()
    {
        Detects("de", "filename");
        _silo.On(HttpMethod.Post, "api/v2/subtitles/upload", ScriptedSilo.Problem(400, "validation_failed"));
        var dialog = Open();
        await dialog.SelectFileAsync(File("Heat.de.srt"));

        await dialog.UploadCommand.ExecuteAsync(null);

        Assert.Equal("Problem detail", dialog.UploadError);
        Assert.Empty(_added);
        Assert.False(dialog.IsUploading);
    }

    [Fact]
    public async Task Search_ListsTheResultsAsTheirRowsShowThem()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "results": [
                { "id": "1", "provider": "opensubtitles", "language": "de", "release_name": "A", "format": "srt", "score": 92, "downloads": 12345, "hearing_impaired": true },
                { "id": "2", "provider": "subdl", "language": "de", "release_name": "B", "format": "srt", "score": 55.5, "downloads": 3, "hearing_impaired": false },
                { "id": "3", "provider": "newone", "language": "de", "release_name": "C", "format": "srt", "score": 10, "downloads": 0, "hearing_impaired": false } ],
              "warnings": [ "Subsource is not configured" ] }
            """));
        var dialog = Open();
        Assert.True(dialog.ShowsSearchHint);

        dialog.SearchLanguage = dialog.SearchLanguages.Single(c => c.Value == "de");
        await dialog.SearchCommand.ExecuteAsync(null);

        Assert.Equal("""{"media_file_id":"42","languages":["de"]}""", _silo.SentTo(HttpMethod.Post, "api/v2/subtitles/search").Single().Body);
        Assert.Equal([("92", "#22c55e", "OS", "#eab308", "↓12,345"), ("55.5", "#eab308", "SDL", "#3b82f6", "↓3"), ("10", "#ef4444", "newone", "#9ca3af", "↓0")],
            dialog.Results.Select(r => (r.ScoreText, r.ScoreColor, r.ProviderText, r.ProviderColor, r.DownloadsText)));
        Assert.Equal(["Subsource is not configured"], dialog.Warnings);
        Assert.False(dialog.ShowsSearchHint);
    }

    [Fact]
    public async Task AResult_IsStored_AndTheDialogIsDone()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "results": [ { "id": "1", "provider": "opensubtitles", "language": "de", "release_name": "A", "format": "srt", "score": 92, "downloads": 1, "hearing_impaired": false } ], "warnings": [] }"""));
        _silo.On(HttpMethod.Post, "api/v2/subtitles/download", ScriptedSilo.Json(HttpStatusCode.OK, SubtitlesApiTests.Stored));
        var dialog = Open();
        await dialog.SearchCommand.ExecuteAsync(null);

        await dialog.DownloadAsync(dialog.Results[0]);

        Assert.Equal("77", Assert.Single(_added).Id);
        Assert.Equal(1, _closes);
    }

    [Fact]
    public async Task AFailedDownload_SaysSo_AndTheRowsCanBeTriedAgain()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "results": [ { "id": "1", "provider": "opensubtitles", "language": "de", "release_name": "A", "format": "srt", "score": 92, "downloads": 1, "hearing_impaired": false } ], "warnings": [] }"""));
        _silo.On(HttpMethod.Post, "api/v2/subtitles/download", ScriptedSilo.NetworkError());
        var dialog = Open();
        await dialog.SearchCommand.ExecuteAsync(null);

        await dialog.DownloadAsync(dialog.Results[0]);

        Assert.Equal("Could not reach the server. Check the connection and try again.", dialog.SearchError);
        Assert.True(dialog.Results[0].CanDownload);
        Assert.False(dialog.IsDownloading);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task WithoutProviders_OnlyUploadIsOffered()
    {
        _silo.On(HttpMethod.Get, "api/v2/subtitles/providers/status", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "allowed": true, "enabled": false, "providers": [], "revision": "1", "schema_version": 1, "state": "not_configured" }"""));

        var dialog = Open();
        await Task.Yield();

        Assert.False(dialog.IsOnlineSearchEnabled);
    }

    [Fact]
    public void AProviderReadThatFails_KeepsTheSearch()
    {
        _silo.On(HttpMethod.Get, "api/v2/subtitles/providers/status", ScriptedSilo.NetworkError());

        var dialog = Open();

        Assert.True(dialog.IsOnlineSearchEnabled);
    }

    [Fact]
    public async Task AnAnswerAfterTheDialogClosed_ChangesNothing()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", () => answer.Task);
        var dialog = Open();
        var search = dialog.SearchCommand.ExecuteAsync(null);

        dialog.CloseCommand.Execute(null);
        dialog.Open("43");
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "results": [ { "id": "1", "provider": "subdl", "language": "en", "release_name": "A", "format": "srt", "score": 1, "downloads": 1, "hearing_impaired": false } ], "warnings": [] }""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        await search;

        Assert.Empty(dialog.Results);
        Assert.False(dialog.IsSearching);
    }

    [Fact]
    public async Task AStoredSubtitleWithoutAnId_ClosesTheDialog_ButNamesNothing()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "results": [ { "id": "1", "provider": "opensubtitles", "language": "de", "release_name": "A", "format": "srt", "score": 92, "downloads": 1, "hearing_impaired": false } ], "warnings": [] }"""));
        _silo.On(HttpMethod.Post, "api/v2/subtitles/download", ScriptedSilo.Json(HttpStatusCode.OK, """{ "subtitle": { "media_file_id": "42" } }"""));
        var dialog = Open();
        await dialog.SearchCommand.ExecuteAsync(null);

        await dialog.DownloadAsync(dialog.Results[0]);

        Assert.Equal(1, _closes);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task APathThatIsNoFile_SaysTheFileCouldNotBeRead()
    {
        var dialog = Open();

        await dialog.SelectFileAsync(Path.Combine(_folder, "missing.srt"));

        Assert.Equal("The file could not be read.", dialog.UploadError);
        Assert.False(dialog.HasFile);
    }
}
