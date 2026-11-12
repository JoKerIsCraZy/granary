using Granary.Core.Downloads;
using Granary.Core.Playback;

namespace Granary.Tests.Downloads;

/// <summary>Which titles play from this PC and which stream: local first, as Silo's Android app, with Granary's version rule.</summary>
public sealed class DownloadPlaybacksTests : IAsyncDisposable
{
    private readonly LocalPlaybackFixture _fixture = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private Task<IServerPlayback?> TryAsync(PlayRequest request) => _fixture.Playbacks.TryCreateAsync(request, LocalPlaybackFixture.Token);

    [Theory]
    // No choice on the title page, or only tracks picked: the download plays.
    [InlineData(null, false, true)]
    [InlineData("4567", false, true)]
    [InlineData("9999", false, true)]
    // The downloaded version picked: the download plays. Another one picked: that one streams.
    [InlineData("4567", true, true)]
    [InlineData("9999", true, false)]
    public async Task APickedVersionThatIsNotTheDownloadedOne_Streams(string? fileId, bool versionPicked, bool playsLocally)
    {
        await _fixture.Harness.AddPlayableAsync();
        var choice = fileId is null ? null : new PrePlayChoice(fileId, versionPicked);

        var playback = await TryAsync(PlayRequest.Resume("ep_5", choice));

        Assert.Equal(playsLocally, playback is LocalPlayback);
    }

    [Fact]
    public async Task ARecordThatDoesNotNameItsVersion_IsCheckedAgainstItsManifest()
    {
        // The server chose the file: the manifest says which one came (4567).
        await _fixture.Harness.AddPlayableAsync(change: r => r with { MediaFileId = null });

        Assert.IsType<LocalPlayback>(await TryAsync(PlayRequest.Resume("ep_5", new PrePlayChoice("4567", IsVersionPicked: true))));
        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5", new PrePlayChoice("9999", IsVersionPicked: true))));
    }

    [Fact]
    public async Task AWatchParty_AlwaysStreams()
    {
        await _fixture.Harness.AddPlayableAsync();

        Assert.Null(await TryAsync(PlayRequest.WatchParty("ep_5", "4567", selectionRevision: 1)));
    }

    [Fact]
    public async Task ATitleWithoutADownload_AndAFilePlayedDirectly_Stream()
    {
        await _fixture.Harness.AddPlayableAsync();

        Assert.Null(await TryAsync(PlayRequest.Resume("ep_6")));
        Assert.Null(await TryAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv")));
    }

    [Theory]
    [InlineData(DownloadState.Queued)]
    [InlineData(DownloadState.Downloading)]
    [InlineData(DownloadState.Failed)]
    [InlineData(DownloadState.Preparing)]
    public async Task ADownloadThatIsNotWhole_Streams(DownloadState state)
    {
        await _fixture.Harness.AddPlayableAsync(change: r => r with { State = state, MediaFile = state == DownloadState.Failed ? "media.mkv" : null });

        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
    }

    [Fact]
    public async Task MissingFiles_Stream()
    {
        var download = await _fixture.Harness.AddPlayableAsync();

        File.Delete(download.MediaPath!);
        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
        Assert.Contains("media file", _fixture.Harness.Logs.All, StringComparison.Ordinal);

        // The whole folder went, as on a drive that is not plugged in.
        Directory.Delete(download.Folder, recursive: true);
        _fixture.Harness.Store.RecheckFolders();
        Assert.True(_fixture.Harness.Find("dl_1")!.FilesMissing);
        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{ "download_id": "dl_other", "content_id": "mv_1" }""")]
    public async Task AManifestThatCannotBeRead_OrIsAnotherDownloads_Streams(string? manifest)
    {
        var download = await _fixture.Harness.AddPlayableAsync();
        string path = Path.Combine(download.Folder, DownloadPaths.Manifest);
        if (manifest is null)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, manifest, LocalPlaybackFixture.Token);
        }

        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
        // The log names the download, never its folder.
        Assert.DoesNotContain(_fixture.Harness.Directory, _fixture.Harness.Logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnotherProfilesDownload_IsNotPlayed()
    {
        await _fixture.Harness.AddPlayableAsync();

        _fixture.Harness.SwitchProfile("8");
        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));

        _fixture.Harness.SignOut();
        Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
    }

    [Fact]
    public async Task ADownloadTheServerNoLongerHas_StillPlays()
    {
        await _fixture.Harness.AddPlayableAsync(change: r => r with { IsNoLongerAvailable = true });

        Assert.IsType<LocalPlayback>(await TryAsync(PlayRequest.Resume("ep_5")));
    }

    [Fact]
    public async Task AFolderThatDoesNotAnswer_HoldsThePlayThreeSecondsAtMost_ThenTheTitleStreams()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };

        var answer = TryAsync(PlayRequest.Resume("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        Assert.False(answer.IsCompleted);
        _fixture.Harness.Time.Advance(DownloadPlaybacks.ReadBudget);

        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        held.Set();
    }

    [Fact]
    public async Task ATitleWithoutADownload_IsAnsweredAtOnce_OnTheCallersThread()
    {
        // The player asks before every stream: nothing may wait for that.
        await _fixture.Harness.Store.LoadAsync(LocalPlaybackFixture.Token);

        Assert.True(TryAsync(PlayRequest.Resume("mv_none")).IsCompletedSuccessfully);
        Assert.True(TryAsync(PlayRequest.WatchParty("ep_5", null, 1)).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ATitleStartedBeforeTheDownloadsWereRead_WaitsForThem()
    {
        await _fixture.Harness.AddPlayableAsync();
        var playbacks = new DownloadPlaybacks(_fixture.Harness.UnreadStore(), _fixture.Harness.Session, _fixture.Create, _fixture.Harness.Time,
            _fixture.Harness.Logs.For<DownloadPlaybacks>());

        Assert.IsType<LocalPlayback>(await playbacks.TryCreateAsync(PlayRequest.Resume("ep_5"), LocalPlaybackFixture.Token));
    }
}
