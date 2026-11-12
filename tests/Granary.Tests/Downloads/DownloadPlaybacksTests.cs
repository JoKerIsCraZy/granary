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
        // The play found it: the download's row says so from now on, and the next play does not look again.
        Assert.True(_fixture.Harness.Find("dl_1")!.MediaMissing);
        Assert.Null(_fixture.Playbacks.Find(PlayRequest.Resume("ep_5")));

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

    [Fact]
    public async Task PlayPressedAgainWhileAFolderDoesNotAnswer_WaitsForTheSameRead_NotOneMore()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        int reads = 0;
        _fixture.Playbacks.Reading = () =>
        {
            Interlocked.Increment(ref reads);
            held.Wait(TimeSpan.FromSeconds(10));
        };

        // Three presses on a share that does not answer: each gives up after the budget.
        foreach (int _ in Enumerable.Range(0, 3))
        {
            var start = TryAsync(PlayRequest.Resume("ep_5"));
            await DownloadHarness.Until(() => Volatile.Read(ref reads) == 1, "the folder is being read");
            _fixture.Harness.Time.Advance(DownloadPlaybacks.ReadBudget);
            Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        }

        // One thread waits for the drive, not three.
        Assert.Equal(1, Volatile.Read(ref reads));

        // The drive answers: the next press plays.
        held.Set();
        var played = await TryAsync(PlayRequest.Resume("ep_5")).WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);
        Assert.NotNull(played);
        await played.DisposeAsync();
    }

    [Fact]
    public async Task AFolderReadThatFailsAfterItsPlayGaveUp_IsLogged_NotLeftUnobserved()
    {
        await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("boom in a folder");
        };

        var start = TryAsync(PlayRequest.Resume("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        _fixture.Harness.Time.Advance(DownloadPlaybacks.ReadBudget);
        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        held.Set();

        await DownloadHarness.Until(() => _fixture.Harness.Logs.All.Contains("Reading the folder of download dl_1 failed unexpectedly", StringComparison.Ordinal),
            "the failed read was logged");
        Assert.DoesNotContain("boom in a folder", _fixture.Harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileSwitchedToWhileTheFolderWasRead_DoesNotGetTheOtherProfilesDownload()
    {
        await _fixture.Harness.AddPlayableAsync();
        // Profile 8 becomes the active one while profile 7's folder is read.
        _fixture.Playbacks.Reading = () => _fixture.Harness.SwitchProfile("8");

        var playback = await TryAsync(PlayRequest.Resume("ep_5"));

        // It would record profile 7's progress while profile 8 watches: the title streams for profile 8 instead.
        Assert.Null(playback);
        Assert.False(_fixture.Harness.Playing.Contains(DownloadHarness.Scope7, "dl_1"));
    }

    [Fact]
    public async Task AFolderReadBegunBeforeTheDownloadsBytesWereFetchedAgain_DoesNotSayTheNewFileIsMissing()
    {
        var download = await _fixture.Harness.AddPlayableAsync();
        using var held = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        _fixture.Playbacks.Reading = () =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
        };
        var answer = TryAsync(PlayRequest.Resume("ep_5"));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));

        // The server has new bytes for the download, in another container: the old file went and the new one is whole
        // before the folder answers the read that looks for the old one.
        File.Delete(download.MediaPath!);
        await File.WriteAllBytesAsync(Path.Combine(download.Folder, "media.mp4"), new byte[] { 1, 2, 3 }, LocalPlaybackFixture.Token);
        await _fixture.Harness.Store.UpdateAsync(download.Record.Scope, "dl_1", r => r with { Revision = r.Revision + 1, MediaFile = "media.mp4" },
            LocalPlaybackFixture.Token);
        held.Set();

        // That read found no file of the old name: its play streams, and the row of the new file is left alone.
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token));
        Assert.False(_fixture.Harness.Find("dl_1")!.MediaMissing);
        _fixture.Playbacks.Reading = null;
        var played = await TryAsync(PlayRequest.Resume("ep_5"));
        Assert.NotNull(played);
        await played.DisposeAsync();
    }

    [Fact]
    public async Task AMediaFileThatCannotBeOpened_Streams_ThoughItsFolderListsIt()
    {
        var download = await _fixture.Harness.AddPlayableAsync();

        // Something holds the file for itself; a file this account may not read answers the same way.
        using (new FileStream(download.MediaPath!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(await TryAsync(PlayRequest.Resume("ep_5")));
        }

        Assert.Contains("cannot be read", _fixture.Harness.Logs.All, StringComparison.Ordinal);
        // The file is there: the download is not marked as missing it, and plays once it can be opened.
        Assert.False(_fixture.Harness.Find("dl_1")!.MediaMissing);
        var played = await TryAsync(PlayRequest.Resume("ep_5"));
        Assert.NotNull(played);
        await played.DisposeAsync();
    }
}
