using System.Net;
using System.Text.Json;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Starting, pausing, resuming and deleting downloads: one create per title however often Download is pressed, the entry a
/// create left behind taken over, seasons one episode at a time, and the server told of a delete once the files are gone.
/// </summary>
public sealed class DownloadManagerTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";
    private static readonly byte[] Source = DownloadPayloads.Source(3000);

    private static readonly DownloadRequest Movie = new("mv_1", null, "4567", "original", "Example Movie", null, null, null);
    private static readonly DownloadRequest Episode = new("ep_5", "sr_1", null, "original", "Chapter Five", "Example Series", 1, 5);

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private void Creates(params Func<Task<HttpResponseMessage>>[] answers) => _h.Silo.On(HttpMethod.Post, CreatePath, answers);

    private static Func<Task<HttpResponseMessage>> Created(string entry) =>
        ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, entry));

    private IReadOnlyList<SentRequest> CreatesSent => _h.Silo.SentTo(HttpMethod.Post, CreatePath);

    private IReadOnlyList<SentRequest> DeletesSent(string id) => _h.Silo.SentTo(HttpMethod.Delete, EntryPath(id));

    [Fact]
    public async Task Start_CreatesTheEntry_AndQueuesIt()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", size: 8_589_934_592)));
        var manager = _h.NewManager();

        var result = await manager.StartAsync(Movie, Token);

        Assert.Equal(DownloadStartResult.Started, result);
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((DownloadState.Queued, "Example Movie", "mv_1", 8_589_934_592L, "4567"),
            (record.State, record.Title, record.MediaItemId, record.ExpectedBytes, record.MediaFileId));
        var sent = Assert.Single(CreatesSent);
        Assert.Equal("7", sent.AsProfile);
        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        Assert.Equal(("mv_1", "4567", "original", 0), (root.GetProperty("content_id").GetString(), root.GetProperty("media_file_id").GetString(),
            root.GetProperty("quality").GetString(), root.GetProperty("expected_revision").GetInt32()));
        Assert.False(root.TryGetProperty("episode_id", out _));
        var caps = root.GetProperty("caps");
        Assert.Equal(("2160p", false), (caps.GetProperty("max_resolution").GetString(), caps.GetProperty("hdr").GetBoolean()));
        Assert.Contains("h264", caps.GetProperty("codecs_video").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task APreparingEntry_WaitsAsPreparing()
    {
        Creates(Created(DownloadPayloads.Entry("dl_5", "preparing", size: 9000, episodeId: "ep_5")));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Started, await manager.StartAsync(Episode, Token));

        var record = _h.Find("dl_5")!.Record;
        Assert.Equal((DownloadState.Preparing, "sr_1", "Example Series", 1, 5), (record.State, record.SeriesId, record.SeriesTitle, record.SeasonNumber, record.EpisodeNumber));
        using var body = JsonDocument.Parse(Assert.Single(CreatesSent).Body!);
        Assert.Equal(("sr_1", "ep_5"), (body.RootElement.GetProperty("content_id").GetString(), body.RootElement.GetProperty("episode_id").GetString()));
    }

    [Fact]
    public async Task PressedTwice_SendsOneCreate()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Creates(async () =>
        {
            await answer.Task;
            return await Created(DownloadPayloads.Entry("dl_1", "ready"))();
        });
        var manager = _h.NewManager();

        var first = manager.StartAsync(Movie, Token);
        await Until(() => CreatesSent.Count == 1, "the create went");
        var second = await manager.StartAsync(Movie, Token);
        answer.SetResult();

        Assert.Equal(DownloadStartResult.AlreadyDownloading, second);
        Assert.Equal(DownloadStartResult.Started, await first);
        Assert.Equal(DownloadStartResult.AlreadyDownloading, await manager.StartAsync(Movie, Token));
        Assert.Single(CreatesSent);
        Assert.Single(_h.Store.All);
    }

    [Fact]
    public async Task AnUncertainCreate_TakesTheEntryTheServerMade()
    {
        Creates(() => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "The answer broke off.")));
        _h.List(DownloadPayloads.Entry("dl_8", "ready", episodeId: "ep_4"), DownloadPayloads.Entry("dl_9", "preparing", episodeId: "ep_5"));
        var manager = _h.NewManager();

        var result = await manager.StartAsync(Episode, Token);

        Assert.Equal(DownloadStartResult.Started, result);
        var record = Assert.Single(_h.Store.All).Record;
        Assert.Equal(("dl_9", DownloadState.Preparing, "ep_5"), (record.DownloadId, record.State, record.MediaItemId));
        Assert.Single(CreatesSent);
    }

    [Fact]
    public async Task ACreateTheServerLostTrackOf_Fails()
    {
        Creates(ScriptedSilo.Problem(502, "bad_gateway"));
        _h.List(DownloadPayloads.Entry("dl_8", "ready", episodeId: "ep_4"));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Failed, await manager.StartAsync(Episode, Token));

        Assert.Empty(_h.Store.All);
        Assert.Single(CreatesSent);
    }

    [Theory]
    [InlineData(403, "permission_denied")]
    [InlineData(404, "not_found")]
    [InlineData(501, "capability_unsupported")]
    public async Task ACreateTheServerRefuses_IsUnavailable(int status, string code)
    {
        Creates(ScriptedSilo.Problem(status, code));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Unavailable, await manager.StartAsync(Movie, Token));

        Assert.Empty(_h.Store.All);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, ListPath));
    }

    [Fact]
    public async Task ASkippedEpisode_IsUnavailable()
    {
        Creates(ScriptedSilo.Json(HttpStatusCode.Accepted,
            """{ "items": [], "skipped": [{ "episode_id": "ep_5", "reason": "quality_unavailable" }], "page": { "has_more": false } }"""));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Unavailable, await manager.StartAsync(Episode, Token));
        Assert.Empty(_h.Store.All);
    }

    [Fact]
    public async Task ASeason_SendsOneCreatePerEpisodeNotYetDownloaded()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1");
        Creates(Created(DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_2")), Created(DownloadPayloads.Entry("dl_3", "ready", episodeId: "ep_3")));
        var manager = _h.NewManager();
        var season = new SeasonDownloadRequest("sr_1", "Example Series", 1,
            [new("ep_1", 1, "Pilot"), new("ep_2", 2, "Chapter Two"), new("ep_3", 3, "Chapter Three")], "10mbps");

        var result = await manager.StartSeasonAsync(season, Token);

        Assert.Equal(new SeasonStartResult(Started: 2, AlreadyThere: 1, Failed: 0), result);
        var bodies = CreatesSent.Select(r => JsonDocument.Parse(r.Body!).RootElement).ToList();
        Assert.Equal(["ep_2", "ep_3"], bodies.Select(b => b.GetProperty("episode_id").GetString()));
        Assert.All(bodies, b => Assert.Equal(("sr_1", "10mbps", 0), (b.GetProperty("content_id").GetString(), b.GetProperty("quality").GetString(),
            b.GetProperty("expected_revision").GetInt32())));
        var third = _h.Find("dl_3")!.Record;
        Assert.Equal(("Chapter Three", 3, 1, "Example Series"), (third.Title, third.EpisodeNumber, third.SeasonNumber, third.SeriesTitle));
    }

    [Fact]
    public async Task Pause_StopsTheTransfer_AndResumeCarriesOn()
    {
        await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source, "\"v1\"",
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1024..], "\"v1\"", 1024, 3000));
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        await manager.PauseAsync("dl_1");
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Queued, "the transfer stopped");
        await SettleAsync(queue);
        var paused = Assert.Single(manager.Items);
        Assert.Equal(("Paused", 1024L), (DownloadText.Status(paused), paused.BytesDone));
        Assert.Single(_h.FileRequests("dl_1"));

        await manager.ResumeAsync("dl_1");
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Completed, "it completed");
        Assert.Equal("bytes=1024-", _h.FileRequests("dl_1")[^1].Range);
        Assert.Equal("Downloaded", DownloadText.Status(manager.ForItem("mv_1")!));
    }

    [Fact]
    public async Task AFullDrive_FailsTheDownload_AndResumeCarriesOnFromItsBytes()
    {
        var stored = await _h.AddAsync("dl_1", validator: "\"v1\"");
        await File.WriteAllBytesAsync(stored.PartPath, Source[..1000], Token);
        _h.ServeFile("dl_1", DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v1\"", 1000, 3000));
        _h.Disk.Set(DownloadTransfer.SpaceReserve + 100);
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Failed, "it failed");

        var failed = manager.ForItem("mv_1")!;
        Assert.Equal(("Failed · not enough space", 1000L), (DownloadText.Status(failed), failed.BytesDone));
        Assert.Empty(_h.FileRequests("dl_1"));

        _h.Disk.Set(1L << 40);
        await manager.ResumeAsync("dl_1");
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Completed, "it completed");
        var resumed = Assert.Single(_h.FileRequests("dl_1"));
        Assert.Equal(("bytes=1000-", "\"v1\""), (resumed.Range, resumed.IfRange));
        Assert.Equal(Source, await File.ReadAllBytesAsync(_h.Find("dl_1")!.MediaPath!, Token));
    }

    [Fact]
    public async Task Resume_TriesAFailedDownloadAgain()
    {
        await _h.AddAsync("dl_1", DownloadState.Failed, change: r => r with { Failure = DownloadFailure.NotEnoughSpace });
        var manager = _h.NewManager();

        await manager.ResumeAsync("dl_1");

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((DownloadState.Queued, DownloadFailure.None), (record.State, record.Failure));
    }

    [Fact]
    public async Task Delete_RemovesTheFilesFirst_ThenTellsTheServer()
    {
        var stored = await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source);
        var folderThere = new List<bool>();
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            lock (folderThere)
            {
                folderThere.Add(Directory.Exists(stored.Folder));
            }
            return ScriptedSilo.NoContent()();
        });
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        await manager.DeleteAsync("dl_1", Token);

        Assert.Null(_h.Find("dl_1"));
        await Until(() => !Directory.Exists(stored.Folder), "the folder went");
        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the server heard");
        lock (folderThere)
        {
            Assert.NotEmpty(folderThere);
            Assert.DoesNotContain(true, folderThere);
        }
        Assert.All(DeletesSent("dl_1"), r => Assert.Equal("7", r.AsProfile));
        Assert.Empty(manager.Items);
    }

    [Fact]
    public async Task ADeleteWhileOffline_IsSentOnceTheServerAnswers()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), ScriptedSilo.NoContent());
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        await SettleAsync(queue);

        await manager.DeleteAsync("dl_1", Token);
        await Until(() => DeletesSent("dl_1").Count == 1, "the server was asked");
        await SettleAsync(queue);
        Assert.Null(_h.Find("dl_1"));
        Assert.Equal([new PendingDelete(Scope7, "dl_1")], _h.Store.PendingDeletes(Scope7));

        // A wake before the retry delay asks nothing.
        queue.Wake();
        await SettleAsync(queue);
        Assert.Single(DeletesSent("dl_1"));

        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => DeletesSent("dl_1").Count == 2, "asked again after 1 s");
        await SettleAsync(queue);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        Assert.Equal(2, DeletesSent("dl_1").Count);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the delete went");
        Assert.Equal(3, DeletesSent("dl_1").Count);
    }

    [Fact]
    public async Task ADeleteForAnotherProfile_WaitsForThatProfile()
    {
        await _h.AddAsync("dl_8", DownloadState.Completed, scope: Scope8);
        await _h.Store.RemoveAsync(Scope8, "dl_8", queueServerDelete: true, Token);
        var queue = _h.StartQueue();
        await SettleAsync(queue);

        Assert.Empty(DeletesSent("dl_8"));
        Assert.Single(_h.Store.PendingDeletes(Scope8));

        _h.SwitchProfile("8");
        await Until(() => _h.Store.PendingDeletes(Scope8).Count == 0, "profile 8's delete went");
        Assert.Equal("8", Assert.Single(DeletesSent("dl_8")).AsProfile);
    }

    [Fact]
    public async Task DeletingARowWhoseFilesAreMissing_ForgetsIt_AndTellsTheServer()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed);
        string away = stored.Folder + "-away";
        Directory.Move(stored.Folder, away);
        _h.Store.RecheckFolders();
        Assert.True(_h.Find("dl_1")!.FilesMissing);
        var manager = _h.NewManager(_h.StartQueue());
        Assert.Equal("Missing file", DownloadText.Status(manager.ForItem("mv_1")!));

        await manager.DeleteAsync("dl_1", Token);

        Assert.Null(_h.Find("dl_1"));
        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the server heard");
        // The store writes the index just after the change holds.
        await Until(() => !IndexNames("dl_1"), "the index forgot it");
        Assert.Single(DeletesSent("dl_1"));
        Assert.True(Directory.Exists(away));
    }

    private bool IndexNames(string id)
    {
        try
        {
            return File.ReadAllText(_h.IndexPath).Contains(id, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Being replaced this moment.
            return true;
        }
    }

    [Fact]
    public async Task Items_AreTheActiveProfilesDownloads_NewestFirst_WithTheirProgress()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, change: r => r with { ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg" } });
        await _h.AddAsync("dl_2", mediaItemId: "mv_2");
        await _h.AddAsync("dl_8", scope: Scope8);
        var manager = _h.NewManager();
        int changes = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref changes);

        var items = manager.Items;

        Assert.Equal(["dl_2", "dl_1"], items.Select(i => i.DownloadId));
        Assert.Equal((1.0, true, 3000L), (items[1].Progress, items[1].IsPlayable, items[1].BytesDone));
        Assert.Equal(Path.Combine(_h.Find("dl_1")!.Folder, "poster.jpg"), items[1].PosterPath);
        Assert.Null(items[0].PosterPath);
        Assert.Equal(3000, manager.BytesUsed());

        await _h.Store.UpdateAsync(Scope7, "dl_2", r => r with { IsPausedByViewer = true }, Token);
        Assert.True(changes > 0);
        Assert.Null(manager.ForItem("mv_9"));
    }

    [Fact]
    public async Task WithoutAProfile_NothingStarts()
    {
        _h.SignOut();
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Unavailable, await manager.StartAsync(Movie, Token));
        Assert.Empty(manager.Items);
        Assert.Empty(CreatesSent);
    }

    [Fact]
    public async Task DownloadingAgain_WhileTheDeleteWaits_DoesNotTakeOverTheEntryBeingDeleted()
    {
        // A download deleted while the server cannot be reached: its entry is still on the server, its delete waits.
        await _h.AddAsync("dl_9", DownloadState.Completed, mediaItemId: "ep_5");
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_9"), ScriptedSilo.NetworkError(), ScriptedSilo.NoContent());
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        await manager.DeleteAsync("dl_9", Token);
        await Until(() => DeletesSent("dl_9").Count == 1, "the delete was tried");
        await SettleAsync(queue);
        Assert.Single(_h.Store.PendingDeletes(Scope7));

        // Download again: the server still has the entry (409), and the registry lists it.
        Creates(ScriptedSilo.Problem(409, "conflict"), Created(DownloadPayloads.Entry("dl_10", "ready", episodeId: "ep_5")));
        _h.List(DownloadPayloads.Entry("dl_9", "completed", episodeId: "ep_5"));
        var result = await manager.StartAsync(Episode, Token);

        Assert.Equal(DownloadStartResult.Failed, result);
        Assert.Null(_h.Find("dl_9"));
        Assert.Single(_h.Store.PendingDeletes(Scope7));

        // The delete goes once the server answers; then Download makes a new entry.
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.Store.PendingDeletes(Scope7).Count == 0, "the delete went");
        _h.ServeAssets("dl_10");
        _h.ServeFile("dl_10", Source);
        Assert.Equal(DownloadStartResult.Started, await manager.StartAsync(Episode, Token));
        Assert.NotNull(_h.Find("dl_10"));
        Assert.Equal(2, DeletesSent("dl_9").Count);
    }

    [Fact]
    public async Task ACreateAnsweredForAnotherTitle_IsNotKept()
    {
        Creates(Created(DownloadPayloads.Entry("dl_4", "ready", episodeId: "ep_4")));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Failed, await manager.StartAsync(Episode, Token));
        Assert.Empty(_h.Store.All);
    }

    [Fact]
    public async Task AnUncertainCreate_WhoseRegistryCursorComesBack_Fails()
    {
        Creates(() => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "The answer broke off.")));
        _h.ListPages(
            DownloadPayloads.Page("c1", DownloadPayloads.Entry("dl_8", "ready", episodeId: "ep_4")),
            DownloadPayloads.Page("c1"));
        var manager = _h.NewManager();

        Assert.Equal(DownloadStartResult.Failed, await manager.StartAsync(Episode, Token));
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, ListPath).Count);
    }

    [Fact]
    public async Task ASeason_StopsWhenTheProfileIsNoLongerActive()
    {
        Creates(() =>
        {
            _h.SignOut();
            return Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1"))();
        });
        var manager = _h.NewManager();
        var season = new SeasonDownloadRequest("sr_1", "Example Series", 1,
            [new("ep_1", 1, "Pilot"), new("ep_2", 2, "Chapter Two"), new("ep_3", 3, "Chapter Three")], "original");

        var result = await manager.StartSeasonAsync(season, Token);

        Assert.Equal(new SeasonStartResult(Started: 1, AlreadyThere: 0, Failed: 0), result);
        Assert.Single(CreatesSent);
    }

    [Fact]
    public async Task ASeasonCancelledHalfway_KeepsWhatItCounted()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")));
        await _h.Store.LoadAsync(Token);
        // Cancelled once the first episode is kept.
        _h.Store.Changed += (_, _) =>
        {
            if (_h.Find("dl_1") is not null)
            {
                cancel.Cancel();
            }
        };
        var manager = _h.NewManager();
        var season = new SeasonDownloadRequest("sr_1", "Example Series", 1, [new("ep_1", 1, "Pilot"), new("ep_2", 2, "Chapter Two")], "original");

        var result = await manager.StartSeasonAsync(season, cancel.Token);

        Assert.Equal(new SeasonStartResult(Started: 1, AlreadyThere: 0, Failed: 0), result);
        Assert.Single(CreatesSent);
        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task Resume_TriesAWaitingDownloadAtOnce()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(),
            DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        var manager = _h.NewManager(queue);
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the first try");
        await SettleAsync(queue);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.FileRequests("dl_1").Count == 2, "the second try");
        await SettleAsync(queue);

        await manager.ResumeAsync("dl_1");

        await Until(() => _h.FileRequests("dl_1").Count == 3, "the try Resume asked for");
        Assert.Equal(TimeSpan.FromSeconds(1), _h.Time.Elapsed);
    }
}
