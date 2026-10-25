using System.Net;
using System.Text;
using Granary.Core.Api;
using Granary.Core.Api.Subtitles;
using Granary.Tests.Profiles;

namespace Granary.Tests.Api;

/// <summary>The Add Subtitles calls, as Silo's web player sends them (SubtitleSearchModal, SubtitleUploadForm).</summary>
public sealed class SubtitlesApiTests
{
    private readonly ScriptedSilo _silo = new();

    private SubtitlesApi Api => new(_silo.Http);

    internal const string Stored = """
        { "subtitle": { "id": "77", "media_file_id": "42", "provider": "opensubtitles", "language": "de", "format": "srt",
          "release_name": "Heat.1995.1080p", "score": 87.5, "hearing_impaired": false, "created_at": "2026-10-25T12:00:00.000Z",
          "timing": { "offset_ms": 0, "scale": 1 } } }
        """;

    [Fact]
    public async Task Search_SendsTheFileAndOneLanguage_AndReadsResultsAndWarnings()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/search", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "results": [ { "id": "os-1", "provider": "opensubtitles", "language": "de", "release_name": "Heat.1995", "format": "srt",
                             "score": 92, "downloads": 12345, "hearing_impaired": true } ],
              "warnings": [ "SubDL is not responding" ] }
            """));

        var found = await Api.SearchAsync(ScriptedSilo.Base, "42", "de", TestContext.Current.CancellationToken);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.Equal("""{"media_file_id":"42","languages":["de"]}""", sent.Body);
        var result = Assert.Single(found.Results);
        Assert.Equal(("os-1", "opensubtitles", 92.0, 12345L, true), (result.Id, result.Provider, result.Score, result.Downloads, result.HearingImpaired));
        Assert.Equal(["SubDL is not responding"], found.Warnings);
    }

    [Fact]
    public async Task Download_EchoesTheResult_AndReturnsTheStoredSubtitle()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/download", ScriptedSilo.Json(HttpStatusCode.OK, Stored));
        var result = new SubtitleSearchResult
        {
            Id = "os-1", Provider = "opensubtitles", Language = "de", ReleaseName = "Heat.1995", Format = "srt", Score = 92, HearingImpaired = true,
        };

        var stored = await Api.DownloadAsync(ScriptedSilo.Base, "42", result, TestContext.Current.CancellationToken);

        Assert.Equal("""{"media_file_id":"42","provider":"opensubtitles","subtitle_id":"os-1","language":"de","release_name":"Heat.1995","score":92,"hearing_impaired":true}""",
            Assert.Single(_silo.Sent).Body);
        Assert.Equal(("77", "42", "de"), (stored.Id, stored.MediaFileId, stored.Language));
    }

    [Fact]
    public async Task Upload_SendsAFormWithTheFileAndItsLanguage()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/upload", ScriptedSilo.Json(HttpStatusCode.OK, Stored));
        var upload = new SubtitleUpload("42", "Heat.de.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHallo\n"), "de",
            LanguageOverride: true, HearingImpaired: false);

        var stored = await Api.UploadAsync(ScriptedSilo.Base, upload, TestContext.Current.CancellationToken);

        string body = Assert.Single(_silo.Sent).Body!;
        Assert.Contains("name=media_file_id\r\n\r\n42\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=file; filename=Heat.de.srt", body, StringComparison.Ordinal);
        Assert.Contains("Hallo", body, StringComparison.Ordinal);
        Assert.Contains("name=language\r\n\r\nde\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=language_override\r\n\r\ntrue\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=hearing_impaired\r\n\r\nfalse\r\n", body, StringComparison.Ordinal);
        Assert.Equal("77", stored.Id);
    }

    [Fact]
    public async Task DetectLanguage_SendsTheFileAndTheHint()
    {
        _silo.On(HttpMethod.Post, "api/v2/subtitles/detect-language",
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "language": "de", "source": "filename" }"""));

        var detected = await Api.DetectLanguageAsync(ScriptedSilo.Base, "Heat.de.srt", "x"u8.ToArray(), "en", TestContext.Current.CancellationToken);

        string body = Assert.Single(_silo.Sent).Body!;
        Assert.Contains("name=file; filename=Heat.de.srt", body, StringComparison.Ordinal);
        Assert.Contains("name=language\r\n\r\nen\r\n", body, StringComparison.Ordinal);
        Assert.Equal(("de", "filename"), (detected.Language, detected.Source));
    }

    [Fact]
    public async Task ProviderStatus_SaysWhetherSearchIsThere()
    {
        _silo.On(HttpMethod.Get, "api/v2/subtitles/providers/status", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "allowed": true, "enabled": false, "providers": [], "revision": "1", "schema_version": 1, "state": "not_configured" }"""));

        var status = await Api.GetProviderStatusAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        Assert.False(status.Enabled);
        Assert.Equal("not_configured", status.State);
    }
}
