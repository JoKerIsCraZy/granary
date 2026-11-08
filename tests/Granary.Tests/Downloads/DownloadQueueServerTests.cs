using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// What the server says about the downloads: preparing copies and their progress, expired prepared files, revoked entries,
/// new revisions, entries it no longer lists; and the manifest, artwork and subtitles kept beside the media.
/// </summary>
public sealed class DownloadQueueServerTests : IAsyncDisposable
{
    private static readonly byte[] Source = DownloadPayloads.Source(3000);

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private Task CompletedAsync(string id) =>
        Until(() => _h.Find(id)?.Record.State == DownloadState.Completed, id + " completed");

    // A fresh look of the queue, so what it did not start is not taken from a look before the event.
    private static Task IdleAsync(DownloadQueue queue) => SettleAsync(queue);

    private static string File(StoredDownload download, string name) => Path.Combine(download.Folder, name);

    [Fact]
    public async Task APreparingDownload_ShowsTheServersProgress_ThenDownloads()
    {
        await _h.AddAsync("dl_1", DownloadState.Preparing);
        _h.ServeFile("dl_1", Source);
        _h.ListPages(
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "preparing", progress: 0.42)),
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")));

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.PreparationProgress == 0.42, "the server's progress");
        await IdleAsync(queue);
        Assert.Equal(DownloadState.Preparing, _h.Find("dl_1")!.Record.State);
        Assert.Empty(_h.FileRequests("dl_1"));

        await _h.AdvanceAsync(TimeSpan.FromSeconds(9), queue);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, ListPath));
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");

        var order = _h.Silo.Sent.Select(r => r.Uri.AbsolutePath).ToList();
        Assert.True(order.FindIndex(p => p.EndsWith("/dl_1/manifest", StringComparison.Ordinal))
            < order.FindIndex(p => p.EndsWith("/dl_1/file", StringComparison.Ordinal)));
        Assert.Null(_h.Find("dl_1")!.Record.PreparationProgress);
    }

    [Fact]
    public async Task AnExpiredPreparedFile_IsPreparedOnce()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(409, "prepared_file_expired"), DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        _h.Silo.On(HttpMethod.Post, PreparePath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "preparing")));
        _h.ListPages(
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")),
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "preparing")),
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")));

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Preparing, "it prepares again");
        await IdleAsync(queue);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, PreparePath("dl_1")));

        await _h.AdvanceAsync(TimeSpan.FromSeconds(10), queue);
        Assert.Equal(DownloadState.Preparing, _h.Find("dl_1")!.Record.State);
        _h.Time.Advance(TimeSpan.FromSeconds(10));
        await CompletedAsync("dl_1");

        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, PreparePath("dl_1")));
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
    }

    [Fact]
    public async Task APause_StopsAPrepareRequestUnderWay()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(409, "prepared_file_expired"));
        _h.Silo.OnCancellable(HttpMethod.Post, PreparePath("dl_1"), async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, PreparePath("dl_1")).Count == 1, "the prepare went");

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsPausedByViewer = true }, Token);

        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Queued, "the transfer stopped");
        await IdleAsync(queue);
        Assert.True(_h.Find("dl_1")!.Record.IsPausedByViewer);
    }

    [Fact]
    public async Task ARevokedDownload_IsDeleted_WhenTheServerSaysSo()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed);
        bool folderGoneFirst = false;
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            folderGoneFirst = !Directory.Exists(stored.Folder);
            return ScriptedSilo.NoContent()();
        });
        _h.List(DownloadPayloads.Entry("dl_1", "revoked"));

        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")).Count == 1, "the server heard");
        await IdleAsync(queue);

        Assert.True(folderGoneFirst);
        Assert.Null(_h.Find("dl_1"));
        Assert.Empty(_h.Store.PendingDeletes(Scope7));
        Assert.Equal("7", _h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1"))[0].AsProfile);
    }

    [Fact]
    public async Task ARevokedDownload_StaysPlayable_WithoutThatFlag()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with { MediaFile = "media.mkv" });
        await System.IO.File.WriteAllBytesAsync(File(stored, "media.mkv"), Source, Token);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.CapabilityWith(revokedRemovesLocal: false)));
        _h.List(DownloadPayloads.Entry("dl_1", "revoked"));

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.IsNoLongerAvailable, "it is no longer available");
        await IdleAsync(queue);

        var download = _h.Find("dl_1")!;
        Assert.Equal(DownloadState.Completed, download.Record.State);
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(download.MediaPath!, Token));
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
    }

    [Fact]
    public async Task ANewRevision_ReplacesTheLocalFiles()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with
        {
            MediaFile = "media.mkv",
            ManifestReadAt = ManualTimeProvider.DefaultStart,
            Validator = "\"v1\"",
        });
        await System.IO.File.WriteAllBytesAsync(File(stored, "media.mkv"), DownloadPayloads.Source(3000, seed: 9), Token);
        await System.IO.File.WriteAllTextAsync(File(stored, DownloadPaths.Manifest), DownloadPayloads.Manifest("dl_1"), Token);
        _h.List(DownloadPayloads.Entry("dl_1", "ready", revision: 2));
        var body = _h.ServeGated("dl_1", Source, "\"v2\"");

        _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the new file is asked for");

        Assert.False(System.IO.File.Exists(File(stored, "media.mkv")));
        Assert.Null(_h.FileRequests("dl_1")[0].Range);
        var replaced = _h.Find("dl_1")!.Record;
        Assert.Equal((2L, (string?)null), (replaced.Revision, replaced.MediaFile));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, ManifestPath("dl_1")));

        body.ReleaseAll();
        await CompletedAsync("dl_1");
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(_h.Find("dl_1")!.MediaPath!, Token));
        Assert.Contains("\"revision\":2", _h.Reports("dl_1")[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadTheServerNoLongerLists_IsNoLongerAvailable()
    {
        await _h.AddAsync("dl_1");
        await _h.AddAsync("dl_2", DownloadState.Completed);
        _h.ServeFile("dl_1", Source);
        _h.List();

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.IsNoLongerAvailable, "it is no longer available");
        await IdleAsync(queue);

        Assert.Equal((DownloadState.Failed, DownloadFailure.Server), (_h.Find("dl_1")!.Record.State, _h.Find("dl_1")!.Record.Failure));
        Assert.Empty(_h.FileRequests("dl_1"));
        var completed = _h.Find("dl_2")!.Record;
        Assert.Equal((DownloadState.Completed, false), (completed.State, completed.IsNoLongerAvailable));
    }

    [Fact]
    public async Task EveryPage_IsReadBeforeAnythingCountsAsGone()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        await _h.AddAsync("dl_2", DownloadState.Completed);
        _h.ListPages(
            DownloadPayloads.Page("next-1", DownloadPayloads.Entry("dl_2", "revoked")),
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "completed")));

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_2") is null, "the revoked one went");
        await IdleAsync(queue);

        var lists = _h.Silo.SentTo(HttpMethod.Get, ListPath);
        Assert.Equal(2, lists.Count);
        Assert.Contains("cursor=next-1", lists[1].Uri.Query, StringComparison.Ordinal);
        Assert.False(_h.Find("dl_1")!.Record.IsNoLongerAvailable);
    }

    [Fact]
    public async Task AListThatFails_JudgesNothing()
    {
        await _h.AddAsync("dl_1", DownloadState.Preparing);
        _h.Silo.On(HttpMethod.Get, ListPath, ScriptedSilo.Problem(503, "dependency_unavailable"));

        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count == 1, "the registry was asked");
        await IdleAsync(queue);

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((DownloadState.Preparing, false), (record.State, record.IsNoLongerAvailable));
    }

    [Fact]
    public async Task TheManifestArtworkAndSubtitles_AreKeptBesideTheMedia()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);

        _h.StartQueue();
        await CompletedAsync("dl_1");

        Assert.Equal(DownloadPayloads.Utf8(DownloadPayloads.Manifest("dl_1")), await System.IO.File.ReadAllBytesAsync(File(stored, DownloadPaths.Manifest), Token));
        foreach (string name in new[] { "poster.jpg", "backdrop.jpg", "series_poster.jpg" })
        {
            Assert.Equal(PosterBytes, await System.IO.File.ReadAllBytesAsync(File(stored, name), Token));
        }
        Assert.Equal(SubtitleBytes, await System.IO.File.ReadAllBytesAsync(File(stored, "sub-0.srt"), Token));
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal("media.mkv", record.MediaFile);
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(File(stored, "media.mkv"), Token));
        Assert.Equal("poster.jpg", record.ArtworkFiles["poster"]);
        Assert.Equal("r1", record.SubtitleRevisions["external:0"]);
        Assert.Equal(ManualTimeProvider.DefaultStart, record.ManifestReadAt);
        Assert.All(_h.Silo.Sent.Where(r => r.Uri.AbsolutePath.Contains("/dl_1/", StringComparison.Ordinal)), r => Assert.Equal("7", r.AsProfile));
    }

    [Fact]
    public async Task AMissingPoster_DoesNotStopTheDownload()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ArtworkPath("dl_1", "poster"), ScriptedSilo.Problem(404, "not_found"));
        _h.Silo.On(HttpMethod.Get, ArtworkPath("dl_1", "backdrop"), ScriptedSilo.Problem(503, "dependency_unavailable", TimeSpan.FromSeconds(5)));
        _h.ServeFile("dl_1", Source);

        _h.StartQueue();
        await CompletedAsync("dl_1");

        Assert.False(System.IO.File.Exists(File(stored, "poster.jpg")));
        var record = _h.Find("dl_1")!.Record;
        Assert.False(record.ArtworkFiles.ContainsKey("poster"));
        Assert.Equal("series_poster.jpg", record.ArtworkFiles["series_poster"]);
    }

    [Fact]
    public async Task ChangedSubtitles_AreFetchedAgain_AfterADay()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        _h.StartQueue();
        await CompletedAsync("dl_1");
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, SubtitlePath("dl_1")));

        byte[] synced = DownloadPayloads.Utf8("1\n00:00:01,500 --> 00:00:02,500\nHello, Elena.\n");
        _h.ServeAssets("dl_1", subtitleRevision: "r2");
        _h.Silo.On(HttpMethod.Get, SubtitlePath("dl_1"), DownloadPayloads.Asset(synced, "application/x-subrip"));
        _h.Time.Advance(TimeSpan.FromHours(23));
        await _h.RestartAsync();
        var queue = _h.StartQueue();
        await IdleAsync(queue);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, SubtitlePath("dl_1")));

        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.RestartAsync();
        queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.SubtitleRevisions.GetValueOrDefault("external:0") == "r2", "the new subtitle was kept");
        await IdleAsync(queue);
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, SubtitlePath("dl_1")).Count);
        Assert.Equal(synced, await System.IO.File.ReadAllBytesAsync(File(stored, "sub-0.srt"), Token));

        _h.Time.Advance(TimeSpan.FromHours(24));
        await _h.RestartAsync();
        queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ManifestPath("dl_1")).Count == 3, "the manifest was read again");
        await IdleAsync(queue);
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, SubtitlePath("dl_1")).Count);
    }

    [Fact]
    public async Task AReadyEntry_RefreshesTheSizeItWasCreatedWith()
    {
        await _h.AddAsync("dl_1", DownloadState.Preparing, size: 9000);
        _h.ServeAssets("dl_1", size: 3000);
        _h.ServeFile("dl_1", Source);
        _h.List(DownloadPayloads.Entry("dl_1", "ready", size: 3000));

        _h.StartQueue();
        await CompletedAsync("dl_1");

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((3000L, DownloadFailure.None), (record.ExpectedBytes, record.Failure));
    }

    [Fact]
    public async Task AServerThatSendsFilesThroughProxies_IsAskedThroughFileProxy()
    {
        await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.CapabilityWith(proxyDelivery: true)));
        _h.Silo.On(HttpMethod.Get, "api/v2/downloads/dl_1/file-proxy", DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));

        _h.StartQueue();
        await CompletedAsync("dl_1");

        Assert.Empty(_h.FileRequests("dl_1"));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, "api/v2/downloads/dl_1/file-proxy"));
    }

    [Fact]
    public async Task AFileTheServerNoLongerHas_IsLookedUp_AndNoLongerAvailable()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(404, "not_found"));
        _h.ListPages(DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")), DownloadPayloads.Page(null));

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.IsNoLongerAvailable, "it is no longer available");
        await IdleAsync(queue);

        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, ListPath).Count);
        Assert.Single(_h.FileRequests("dl_1"));
        Assert.Equal(DownloadState.Failed, _h.Find("dl_1")!.Record.State);
    }

    [Fact]
    public async Task ANewRevisionAsTheOldTransferFinishes_DeletesTheFileThatTransferNamed()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        DownloadQueue? queue = null;
        Task? reconcile = null;
        int replaced = 0;
        // As the old transfer puts its last bytes on the disk, the server has new ones (in an MP4 now): a reconcile replaces the
        // download while that transfer finishes, names its file media.mkv and says so.
        queue = _h.StartQueue(flushed: _ =>
        {
            if (Interlocked.Exchange(ref replaced, 1) == 0)
            {
                _h.List(DownloadPayloads.Entry("dl_1", "ready", revision: 2));
                _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_1", "mp4")));
                reconcile = Task.Run(() => queue!.ReconcileAsync(Token));
                SpinWait.SpinUntil(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count >= 2, TimeSpan.FromSeconds(2));
                Thread.Sleep(200);
            }
        });

        await Until(() => _h.Find("dl_1")?.Record is { Revision: 2, State: DownloadState.Completed }, "the new revision completed");
        await reconcile!;

        Assert.Equal("media.mp4", _h.Find("dl_1")!.Record.MediaFile);
        Assert.False(System.IO.File.Exists(File(stored, "media.mkv")));
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(File(stored, "media.mp4"), Token));
    }

    [Fact]
    public async Task ANewRevision_IsReportedBeforeItsFile_ThoughTheOldReportWaitsItsDelay()
    {
        await _h.AddAsync("dl_1");
        _h.ServeGated("dl_1", Source, "\"v1\"", DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v2\""));
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable"),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading", revision: 2)));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the first revision's file was asked for, its report not sent");

        _h.List(DownloadPayloads.Entry("dl_1", "ready", revision: 2));
        await queue.ReconcileAsync(Token);

        await Until(() => _h.FileRequests("dl_1").Count == 2, "the new revision's file was asked for");
        Assert.Equal(2, _h.Reports("dl_1").Count);
        Assert.Contains("\"revision\":2", _h.Reports("dl_1")[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActivationThatFails_IsDoneAgain_CapabilityAndRegistry()
    {
        await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => throw new InvalidOperationException("The test's server broke."),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.CapabilityWith(proxyDelivery: true)));
        _h.Silo.On(HttpMethod.Get, "api/v2/downloads/dl_1/file-proxy", DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.Logs.All.Contains("A pass of the download queue failed", StringComparison.Ordinal), "the activation failed");

        // The next pass, a minute later at the latest, activates the profile again.
        await _h.AdvanceAsync(DownloadQueue.RetryDelays[^1], queue);

        await CompletedAsync("dl_1");
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, ListPath));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, "api/v2/downloads/dl_1/file-proxy"));
        Assert.Empty(_h.FileRequests("dl_1"));
    }

    [Fact]
    public async Task ARefusalWhoseRegistryReadFails_IsLookedUpAgainLater()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(404, "not_found"));
        _h.Silo.On(HttpMethod.Get, ListPath,
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready"))),
            ScriptedSilo.Problem(503, "dependency_unavailable"),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Page(null)));

        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count == 2, "the registry was read after the refusal");
        await IdleAsync(queue);
        Assert.False(_h.Find("dl_1")!.Record.IsNoLongerAvailable);

        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.Find("dl_1")!.Record.IsNoLongerAvailable, "it was looked up again");
        Assert.Equal(3, _h.Silo.SentTo(HttpMethod.Get, ListPath).Count);
    }

    [Fact]
    public async Task AReconcileFromOutside_ThatReplacesARevision_StartsTheNewTransfer()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with
        {
            MediaFile = "media.mkv",
            ManifestReadAt = ManualTimeProvider.DefaultStart,
        });
        await System.IO.File.WriteAllBytesAsync(File(stored, "media.mkv"), DownloadPayloads.Source(3000, seed: 9), Token);
        _h.ServeFile("dl_1", Source, "\"v2\"");
        var queue = _h.StartQueue();
        await IdleAsync(queue);
        // The replace holds until the loop has made the pass its change woke, which passes the download over while it is
        // being replaced: only a wake once the replace is done starts it.
        int held = 0;
        _h.Store.Changed += (_, _) =>
        {
            if (_h.Find("dl_1")?.Record.Revision == 2 && Interlocked.Exchange(ref held, 1) == 0)
            {
                long passes = queue.Passes;
                SpinWait.SpinUntil(() => queue.Passes > passes, TimeSpan.FromSeconds(2));
            }
        };

        _h.List(DownloadPayloads.Entry("dl_1", "ready", revision: 2));
        await queue.ReconcileAsync(Token);

        await CompletedAsync("dl_1");
        Assert.Equal(2, _h.Find("dl_1")!.Record.Revision);
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(_h.Find("dl_1")!.MediaPath!, Token));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task ANewRevisionThatIsOver_KeepsThePlayableCopy(string status)
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with
        {
            MediaFile = "media.mkv",
            ManifestReadAt = ManualTimeProvider.DefaultStart,
        });
        await System.IO.File.WriteAllBytesAsync(File(stored, "media.mkv"), Source, Token);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.CapabilityWith(revokedRemovesLocal: false)));
        _h.List(DownloadPayloads.Entry("dl_1", status, revision: 2));

        var queue = _h.StartQueue();
        await IdleAsync(queue);

        var download = _h.Find("dl_1")!;
        Assert.Equal((DownloadState.Completed, 1L), (download.Record.State, download.Record.Revision));
        Assert.Equal(Source, await System.IO.File.ReadAllBytesAsync(download.MediaPath!, Token));
        Assert.Equal(status != "failed", download.Record.IsNoLongerAvailable);
        Assert.Empty(_h.FileRequests("dl_1"));
    }

    [Fact]
    public async Task ARegistryWhoseCursorComesBack_IsNotReadForever_NorTakenAsWhole()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        await _h.AddAsync("dl_2", DownloadState.Preparing);
        _h.ListPages(
            DownloadPayloads.Page("c1", DownloadPayloads.Entry("dl_1", "completed")),
            DownloadPayloads.Page("c1"));

        var queue = _h.StartQueue();
        await IdleAsync(queue);

        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, ListPath).Count);
        Assert.Equal((DownloadState.Preparing, false), (_h.Find("dl_2")!.Record.State, _h.Find("dl_2")!.Record.IsNoLongerAvailable));
    }

    [Fact]
    public async Task AManifestOfAnotherDownload_FailsTheDownload()
    {
        await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Manifest("dl_2")));
        _h.ServeFile("dl_1", Source);

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Failed, "it failed");
        await IdleAsync(queue);

        Assert.Equal(DownloadFailure.Server, _h.Find("dl_1")!.Record.Failure);
        Assert.Empty(_h.FileRequests("dl_1"));
        Assert.False(System.IO.File.Exists(File(_h.Find("dl_1")!, DownloadPaths.Manifest)));
    }

    [Fact]
    public async Task AManifestTheServerRefuses_FailsTheDownload_AndLooksItUp()
    {
        await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, ManifestPath("dl_1"), ScriptedSilo.Problem(409, "conflict"));
        _h.ServeFile("dl_1", Source);

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Failed, "it failed");
        await IdleAsync(queue);

        Assert.Equal(DownloadFailure.Server, _h.Find("dl_1")!.Record.Failure);
        Assert.Empty(_h.FileRequests("dl_1"));
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, ListPath).Count);
    }

    [Fact]
    public async Task ADeleteWhoseFolderIsHeld_GoesOnceTheRecheckDeletedTheFolder()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed);
        var queue = _h.StartQueue();
        await IdleAsync(queue);
        await using (new FileStream(System.IO.Path.Combine(stored.Folder, "media.mkv"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await _h.Store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);
            await IdleAsync(queue);
            await IdleAsync(queue);
            Assert.Empty(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
        }

        _h.Time.Advance(DownloadQueue.FolderRecheck);

        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the delete went");
        Assert.False(Directory.Exists(stored.Folder));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
    }

    [Fact]
    public async Task ADeleteRefusedForTheSignIn_IsKeptAndSentLater()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), ScriptedSilo.Problem(401, "session_expired"), ScriptedSilo.NoContent());
        await _h.Store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")).Count == 1, "the server was asked");
        await IdleAsync(queue);
        Assert.Single(_h.Store.PendingDeletes(Scope7));

        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the delete went");
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")).Count);
    }
}
