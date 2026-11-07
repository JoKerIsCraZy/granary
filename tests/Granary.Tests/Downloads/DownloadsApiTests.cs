using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>The server's downloads calls (downloads-api.md, sections 3 to 6), each sent as the profile it was made for.</summary>
public sealed class DownloadsApiTests
{
    private const string Profile = "7";
    private const string FilePath = "api/v2/downloads/dl_1/file";
    private const string ProxyPath = "api/v2/downloads/dl_1/file-proxy";

    private readonly ScriptedSilo _silo = new();
    private readonly FakeHttpMessageHandler _hop;
    private readonly FakeHttpClients _clients;
    private Func<Task<HttpResponseMessage>> _hopAnswer = () => throw new InvalidOperationException("No hop was expected");

    public DownloadsApiTests()
    {
        _hop = new FakeHttpMessageHandler((_, _) => _hopAnswer());
        _clients = new FakeHttpClients(_hop);
    }

    private DownloadsApi Api => new(_silo.Http, _clients);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Capability_IsUsableOnlyWhenEnabledAndAllowed()
    {
        _silo.On(HttpMethod.Get, "api/v2/capabilities/downloads",
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: true, allowed: false)),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: false, allowed: true)),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()));

        var notAllowed = await Api.GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);
        var disabled = await Api.GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);
        var usable = await Api.GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);

        Assert.False(notAllowed.IsUsable);
        Assert.False(disabled.IsUsable);
        Assert.True(usable.IsUsable);
        Assert.True(usable.RevokedRemovesLocal);
        Assert.False(usable.ProxyDelivery);
    }

    [Fact]
    public async Task Capability_ReadsTheQualityOptions()
    {
        _silo.On(HttpMethod.Get, "api/v2/capabilities/downloads",
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(true, true, "original", "10mbps")));

        var capability = await Api.GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);

        Assert.Equal(["original", "10mbps"], capability.QualityPresets);
        Assert.Equal(
            [new DownloadQualityOption("original", null, null), new DownloadQualityOption("10mbps", 10000, 1080)],
            capability.QualityOptions);
    }

    [Fact]
    public async Task Create_SendsTheQualityTheCapsAndARevisionOfZero()
    {
        _silo.On(HttpMethod.Post, "api/v2/downloads",
            ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_5"))));
        var facts = new PlaybackDeviceFacts { Platform = "windows", HardwareVideoCodecs = ["h264", "hevc"] };
        var body = new CreateDownloadBody
        {
            ContentId = "sr_1",
            EpisodeId = "ep_5",
            MediaFileId = "4567",
            Quality = DownloadQualities.Original,
            Caps = DownloadCaps.For(facts, softwareDecoding: false),
            ExpectedRevision = 0,
        };

        var created = await Api.CreateAsync(ScriptedSilo.Base, Profile, body, Token);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(Profile, sent.AsProfile);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.StartsWith("""{"content_id":"sr_1","episode_id":"ep_5","media_file_id":"4567","quality":"original","caps":{"codecs_video":["h264","hevc"],""",
            sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"codecs_audio\":[\"aac\",", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"containers\":[\"mkv\",\"mp4\",", sent.Body, StringComparison.Ordinal);
        Assert.EndsWith(""","max_resolution":"2160p","hdr":false},"expected_revision":0}""", sent.Body, StringComparison.Ordinal);
        var entry = Assert.Single(created.Items);
        Assert.Equal(("dl_1", "sr_1", "ep_5", DownloadStatuses.Ready, 1L), (entry.Id, entry.ContentId, entry.EpisodeId, entry.Status, entry.Revision));
        Assert.Empty(created.Skipped);
    }

    [Fact]
    public async Task EveryCall_GoesAsTheProfileItWasMadeFor()
    {
        string entry = DownloadPayloads.Entry("dl_1", "ready");
        _silo.On(HttpMethod.Get, "api/v2/capabilities/downloads", ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()))
            .On(HttpMethod.Post, "api/v2/downloads", ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, entry)))
            .On(HttpMethod.Get, "api/v2/downloads", ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Page(null, entry)))
            .On(HttpMethod.Patch, "api/v2/downloads/dl_1", ScriptedSilo.Json(HttpStatusCode.OK, entry))
            .On(HttpMethod.Post, "api/v2/downloads/dl_1/prepare", ScriptedSilo.Json(HttpStatusCode.OK, entry))
            .On(HttpMethod.Delete, "api/v2/downloads/dl_1", ScriptedSilo.NoContent())
            .On(HttpMethod.Get, "api/v2/downloads/dl_1/manifest", ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1")))
            .On(HttpMethod.Get, "api/v2/downloads/dl_1/artwork/poster", DownloadPayloads.Bytes(HttpStatusCode.OK, [1, 2, 3], "\"a1\""))
            .On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.OK, DownloadPayloads.Source(10), "\"v1\""));
        var server = ScriptedSilo.Base;

        await Api.GetCapabilityAsync(server, Profile, Token);
        await Api.CreateAsync(server, Profile, new CreateDownloadBody { ContentId = "mv_1", Quality = DownloadQualities.Original, ExpectedRevision = 0 }, Token);
        await Api.ListAsync(server, Profile, null, Token);
        await Api.ReportAsync(server, Profile, "dl_1", new DownloadStatusEvent(DownloadStatuses.Downloading, DateTimeOffset.UnixEpoch, 1), Token);
        await Api.PrepareAsync(server, Profile, "dl_1", Token);
        await Api.DeleteAsync(server, Profile, "dl_1", Token);
        await Api.GetManifestAsync(server, Profile, "dl_1", Token);
        (await Api.OpenAssetAsync(server, Profile, "api/v2/downloads/dl_1/artwork/poster", "\"a0\"", Token)).Dispose();
        (await Api.OpenFileAsync(server, Profile, "dl_1", null, viaProxy: false, Token)).Dispose();

        Assert.Equal(9, _silo.Sent.Count);
        Assert.All(_silo.Sent, sent => Assert.Equal((SiloAuth.Profile, Profile), (sent.Auth, sent.AsProfile)));
        Assert.Equal("?limit=100", _silo.SentTo(HttpMethod.Get, "api/v2/downloads").Single().Uri.Query);
        Assert.Equal("\"a0\"", _silo.SentTo(HttpMethod.Get, "api/v2/downloads/dl_1/artwork/poster").Single().IfNoneMatch);
        Assert.Null(_silo.SentTo(HttpMethod.Get, FilePath).Single().Range);
    }

    [Fact]
    public async Task List_FollowsTheCursorItWasGiven()
    {
        _silo.On(HttpMethod.Get, "api/v2/downloads",
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Page("next/page=", DownloadPayloads.Entry("dl_2", "preparing"))));

        var page = await Api.ListAsync(ScriptedSilo.Base, Profile, "abc+/=", Token);

        Assert.Equal("?limit=100&cursor=abc%2B%2F%3D", Assert.Single(_silo.Sent).Uri.Query);
        Assert.Equal(new DownloadPageInfo(true, "next/page="), page.Page);
        Assert.Equal(DownloadStatuses.Preparing, Assert.Single(page.Items).Status);
    }

    [Fact]
    public async Task OpenFile_Past4GB_AsksForTheRestWithIfRange()
    {
        _silo.On(HttpMethod.Get, FilePath,
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, DownloadPayloads.Source(10), "\"v1\"", 5_000_000_000, 5_000_000_010));

        using var response = await Api.OpenFileAsync(ScriptedSilo.Base, Profile, "dl_1", new ByteRange(5_000_000_000, "\"v1\""), viaProxy: false, Token);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal("bytes=5000000000-", sent.Range);
        Assert.Equal("\"v1\"", sent.IfRange);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var range = response.Content.Headers.ContentRange!;
        Assert.Equal((5_000_000_000L, 5_000_000_009L, 5_000_000_010L), (range.From, range.To, range.Length));
    }

    [Fact]
    public async Task AnEntry_KeepsSizesOver4GB()
    {
        _silo.On(HttpMethod.Get, "api/v2/downloads",
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready", size: 8_589_934_592))));

        var page = await Api.ListAsync(ScriptedSilo.Base, Profile, null, Token);

        Assert.Equal(8_589_934_592L, Assert.Single(page.Items).FileSize);
    }

    [Fact]
    public async Task OpenFile_ThroughTheProxy_FollowsOneRedirectWithoutCredentials()
    {
        _silo.On(HttpMethod.Get, ProxyPath, DownloadPayloads.Redirect("https://node.example/dl?t=abc"));
        _hopAnswer = DownloadPayloads.Bytes(HttpStatusCode.PartialContent, DownloadPayloads.Source(10), "\"v1\"", 1000, 1010);

        using (var response = await Api.OpenFileAsync(ScriptedSilo.Base, Profile, "dl_1", new ByteRange(1000, "\"v1\""), viaProxy: true, Token))
        {
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        }

        Assert.Equal("bytes=1000-", Assert.Single(_silo.Sent).Range);
        Assert.Equal([DownloadsApi.HopClientName], _clients.Names);
        var hop = Assert.Single(_hop.Requests);
        Assert.Equal(HttpMethod.Get, hop.Method);
        Assert.Equal(new Uri("https://node.example/dl?t=abc"), hop.RequestUri);
        Assert.Equal("bytes=1000-", hop.Headers.Range?.ToString());
        Assert.Equal("\"v1\"", hop.Headers.IfRange?.ToString());
        Assert.Null(hop.Headers.Authorization);
        Assert.DoesNotContain(hop.Headers, header => header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SiloAuth.Anonymous, hop.GetAuth());
        Assert.Null(hop.GetServer());

        _hopAnswer = DownloadPayloads.Redirect("https://other.example/dl");
        using var second = await Api.OpenFileAsync(ScriptedSilo.Base, Profile, "dl_1", null, viaProxy: true, Token);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, second.StatusCode);
        Assert.Equal(2, _hop.Requests.Count);
    }

    [Fact]
    public async Task OpenFile_ARedirectToThisMachine_IsRefused()
    {
        _silo.On(HttpMethod.Get, ProxyPath, DownloadPayloads.Redirect("http://127.0.0.1/x"));

        var error = await Assert.ThrowsAsync<DownloadRedirectRefusedException>(() =>
            Api.OpenFileAsync(ScriptedSilo.Base, Profile, "dl_1", null, viaProxy: true, Token));

        Assert.Equal(HttpRequestError.InvalidResponse, error.HttpRequestError);
        Assert.DoesNotContain("127.0.0.1", error.Message, StringComparison.Ordinal);
        Assert.Empty(_hop.Requests);
    }

    [Theory]
    [InlineData("\"v1\"", true)]
    [InlineData("Thu, 01 Oct 2026 12:00:00 GMT", true)]
    [InlineData("W/\"v1\"", false)]
    [InlineData("*", false)]
    [InlineData("garbage", false)]
    [InlineData("\"v1\", \"v2\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IfRange_TakesAStrongETagOrADate(string? validator, bool usable) =>
        Assert.Equal(usable, ByteRange.IsValidator(validator));

    [Fact]
    public async Task OpenFile_WithAnUnreadableValidator_IsRefusedUnsent()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Api.OpenFileAsync(ScriptedSilo.Base, Profile, "dl_1", new ByteRange(1000, "garbage"), viaProxy: false, Token));

        Assert.Equal("range", error.ParamName);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AManifestThatStalls_IsGivenUpAfterAMinute()
    {
        var time = new ManualTimeProvider();
        var body = new StallingBody(DownloadPayloads.Utf8("""{ "download_id": "dl_1", """));
        _silo.On(HttpMethod.Get, "api/v2/downloads/dl_1/manifest",
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        var api = new DownloadsApi(_silo.Http, _clients, time);

        var read = api.GetManifestAsync(ScriptedSilo.Base, Profile, "dl_1", Token);
        await body.Stalling.WaitAsync(Token);
        time.Advance(DownloadsApi.ManifestStallTimeout - TimeSpan.FromSeconds(1));

        Assert.False(read.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => read);

        Assert.Equal(HttpRequestError.ResponseEnded, error.HttpRequestError);
    }

    // The bytes it has, then nothing until the reader gives up.
    private sealed class StallingBody(byte[] first) : MemoryStream(first)
    {
        private readonly TaskCompletionSource _stalling = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Stalling => _stalling.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }
            _stalling.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/artwork/poster", "api/v2/downloads/dl_1/artwork/poster")]
    [InlineData("dl_1", "/silo/api/v2/downloads/dl_1/subtitles/external:0", "api/v2/downloads/dl_1/subtitles/external:0")]
    [InlineData("dl_1", "/api/v2/downloads/dl_2/artwork/poster", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/../../auth/logout", null)]
    [InlineData("dl_1", "https://other.example/api/v2/downloads/dl_1/artwork/poster", null)]
    [InlineData("dl_1", "//other.example/api/v2/downloads/dl_1/artwork/poster", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/artwork/..%2F..%2Fauth", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/manifest", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/artwork/poster/extra", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/artwork/", null)]
    [InlineData("dl_1", "/api/v2/downloads/dl_1/artwork/poster?x=1", null)]
    [InlineData("dl_1", null, null)]
    public void AssetPath_TakesOnlyThisDownloadsArtworkAndSubtitles(string downloadId, string? fetchUrl, string? expected) =>
        Assert.Equal(expected, DownloadsApi.AssetPath(downloadId, fetchUrl));

    [Fact]
    public async Task TheManifest_ComesAsSent_AndReadsTheEpisode()
    {
        string json = DownloadPayloads.Manifest("dl_1", "mkv", 8_589_934_592);
        _silo.On(HttpMethod.Get, "api/v2/downloads/dl_1/manifest", ScriptedSilo.Json(HttpStatusCode.OK, json));

        byte[] bytes = await Api.GetManifestAsync(ScriptedSilo.Base, Profile, "dl_1", Token);
        var manifest = OfflineManifest.Read(bytes);

        Assert.Equal(DownloadPayloads.Utf8(json), bytes);
        Assert.Equal(("dl_1", "sr_1", "ep_5", "episode", 1L), (manifest.DownloadId, manifest.ContentId, manifest.EpisodeId, manifest.Type, manifest.Revision));
        Assert.Equal(("Chapter Five", "Example Series", 1, 5), (manifest.Title, manifest.SeriesTitle, manifest.SeasonNumber, manifest.EpisodeNumber));
        Assert.Equal(("mkv", 7200.0, 1), (manifest.Container, manifest.DurationSeconds, manifest.SelectedAudioTrackIndex));
        Assert.Equal(2, manifest.AudioTracks.Count);
        Assert.Equal((1, "Commentary", 2), manifest.AudioTracks.Where(t => !t.Default).Select(t => (t.Index, t.Title, t.Channels)).Single());
        Assert.Equal(new OfflineChapter(0, "Cold Open", 0, 142.5), Assert.Single(manifest.Chapters));
        Assert.Equal([new OfflineMarker("intro", 60, 90), new OfflineMarker("credits", 7100, 7200)], manifest.MarkerSegments);
        var subtitle = Assert.Single(manifest.Subtitles);
        Assert.Equal(("srt", true, "/api/v2/downloads/dl_1/subtitles/external:0", "r1"), (subtitle.Format, subtitle.External, subtitle.FetchUrl, subtitle.Revision));
        Assert.Equal(["backdrop", "poster", "series_poster"], manifest.ArtworkUrls.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(8_589_934_592L, manifest.Integrity!.ExpectedBytes);
        Assert.Equal(3, manifest.ManifestVersion);
    }

    [Fact]
    public async Task AManifestOverOneMiB_IsRefused()
    {
        string huge = $$"""{ "download_id": "dl_1", "overview": "{{new string('x', DownloadsApi.MaxManifestBytes)}}" }""";
        _silo.On(HttpMethod.Get, "api/v2/downloads/dl_1/manifest", ScriptedSilo.Json(HttpStatusCode.OK, huge));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.GetManifestAsync(ScriptedSilo.Base, Profile, "dl_1", Token));

        Assert.Equal(413, error.Status);
    }

    [Fact]
    public async Task Delete_AnEntryTheServerNoLongerHas_CountsAsDone()
    {
        _silo.On(HttpMethod.Delete, "api/v2/downloads/dl_1", ScriptedSilo.Problem(404, ProblemCodes.NotFound));
        _silo.On(HttpMethod.Delete, "api/v2/downloads/dl_2", ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));

        await Api.DeleteAsync(ScriptedSilo.Base, Profile, "dl_1", Token);
        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.DeleteAsync(ScriptedSilo.Base, Profile, "dl_2", Token));

        Assert.Equal(503, error.Status);
    }

    [Theory]
    [InlineData("https://nas.example/silo", "https://node.example/dl?t=abc", "https://node.example/dl?t=abc")]
    [InlineData("http://nas.local:8080", "http://node.local/dl?t=abc", "http://node.local/dl?t=abc")]
    [InlineData("https://nas.example/silo", "http://node.example/dl?t=abc", null)]
    [InlineData("https://nas.example/silo", "https://user:pass@node.example/dl", null)]
    [InlineData("https://nas.example/silo", "https://localhost/dl", null)]
    [InlineData("https://nas.example/silo", "https://169.254.169.254/latest", null)]
    [InlineData("https://nas.example/silo", "https://node.example/a/%2e%2e/b", null)]
    [InlineData("https://nas.example/silo", "ftp://node.example/dl", null)]
    public void TheProxyHop_IsOnlyASecureRemoteAddress(string server, string target, string? expected) =>
        Assert.Equal(expected is null ? null : new Uri(expected), PlaybackUrl.ResolveProxyHop(new Uri(server), new Uri(target)));
}
