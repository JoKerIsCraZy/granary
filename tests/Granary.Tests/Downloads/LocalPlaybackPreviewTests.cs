using System.Globalization;
using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Images;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>
/// The pictures of a download's chapters: the manifest's blurs without the server, and while it answers the chapter
/// pictures of the file the download was made from.
/// </summary>
public sealed class LocalPlaybackPreviewTests : IAsyncDisposable
{
    private readonly LocalPlaybackFixture _fixture = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WithoutTheServer_EachChapterShowsTheManifestsBlur()
    {
        _fixture.GoOffline();

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, "iC")], playback.Chapters);
    }

    [Fact]
    public async Task AnOldManifestWithoutTheBlur_StillPlays_WithThePlaceholder()
    {
        var download = await _fixture.Harness.AddPlayableAsync();
        string manifest = Path.Combine(download.Folder, DownloadPaths.Manifest);
        string older = File.ReadAllText(manifest).Replace(", \"thumbnail_thumbhash\": \"iC\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("thumbnail_thumbhash", older, StringComparison.Ordinal);
        File.WriteAllText(manifest, older);
        _fixture.GoOffline();

        var playback = await _fixture.StartAsync();

        Assert.NotNull(playback);
        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, null)], playback.Chapters);
    }

    [Fact]
    public async Task WithTheServer_AChapterTheWatchDetailMatches_ShowsItsPicture()
    {
        // The server measured the chapter a little differently: within a second it is the same one.
        ServerHas(Chapter(0, 0.6, "/api/v2/chapters/0.webp"));

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, ArtworkUrl.Resolve(ScriptedSilo.Base, "/api/v2/chapters/0.webp"), "iC")],
            playback.Chapters);
    }

    [Theory]
    [InlineData("4567", 1, 0)] // another chapter's index
    [InlineData("4567", 0, 30)] // a rescan moved its start
    [InlineData("9999", 0, 0)] // another file of the title
    public async Task WithTheServer_AChapterTheWatchDetailDoesNotMatch_KeepsItsBlur(string fileId, int index, double start)
    {
        ServerHas(Chapter(index, start, "/api/v2/chapters/0.webp"), fileId: fileId);

        var playback = await _fixture.PlayingAsync();

        Assert.Equal([new PlaybackChapter("Cold Open", 0, 142.5, null, "iC")], playback.Chapters);
    }

    // The episode's watch detail with the file's chapters and whether it has previews.
    private void ServerHas(string chapters = "", bool trickplay = false, string fileId = "4567")
    {
        string version = $$"""
            "file_id": "{{fileId}}", "trickplay_available": {{(trickplay ? "true" : "false")}}, "chapters": [ {{chapters}} ],
            """;
        string watch = LocalPlaybackFixture.Watch().Replace("\"file_id\": \"4567\",", version, StringComparison.Ordinal);
        Assert.Contains("trickplay_available", watch, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(HttpStatusCode.OK, watch));
    }

    private static string Chapter(int index, double start, string thumbnail) => $$"""
        { "index": {{index}}, "title": "Cold Open", "start_seconds": {{start.ToString(CultureInfo.InvariantCulture)}}, "end_seconds": 142.5,
          "source": "embedded", "thumbnail_url": "{{thumbnail}}", "thumbnail_thumbhash": "iC" }
        """;
}
