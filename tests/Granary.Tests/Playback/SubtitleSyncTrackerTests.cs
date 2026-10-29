using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>The timing and sync of the playing file's subtitles, followed as Silo's web player and Apple app follow them.</summary>
public sealed class SubtitleSyncTrackerTests : IDisposable
{
    private const string FileId = "41";
    private const string Stored = "stored-12";
    private const string External = "external-" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";
    private const string StatusPath = "api/v2/subtitles/sync/status";
    private const string ListPath = "api/v2/subtitles/" + FileId + "/sync";
    private const string StoredPath = ListPath + "/" + Stored;
    private const string TimingPath = StoredPath + "/timing";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SubtitleSyncTracker _tracker;
    private readonly List<string> _timingChanges = [];

    public SubtitleSyncTrackerTests()
    {
        _tracker = new SubtitleSyncTracker(new SubtitleSyncApi(_silo.Http), new ImmediateDispatcher(), _time, new LogSink().For<SubtitleSyncTracker>());
        _tracker.TimingChanged += (_, key) => _timingChanges.Add(key);
        _silo.On(HttpMethod.Get, StatusPath, Ok(Status()));
        _silo.On(HttpMethod.Get, ListPath, Ok(List(State(Stored), State(External, source: "external"))));
    }

    public void Dispose() => _tracker.Dispose();

    internal static string Status(bool allowed = true, string state = "available", bool external = true) =>
        $$"""{ "state": "{{state}}", "allowed": {{Bool(allowed)}}, "auto_sync": true, "external": {{Bool(external)}}, "revision": "r1" }""";

    internal static string Job(string id, string status, double? progress = null, string? phase = null, string trigger = "manual",
        string? result = null, string? failure = null, string createdAt = "2026-10-29T10:00:00.000Z")
    {
        var parts = new List<string>
        {
            $"\"id\": \"{id}\"", $"\"status\": \"{status}\"", $"\"trigger\": \"{trigger}\"", "\"confidence\": null",
            $"\"created_at\": \"{createdAt}\"", "\"finished_at\": null",
        };
        if (progress is { } p)
        {
            parts.Add($"\"progress\": {p.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (phase is not null)
        {
            parts.Add($"\"phase\": \"{phase}\"");
        }
        if (result is not null)
        {
            parts.Add($"\"result\": {result}");
        }
        if (failure is not null)
        {
            parts.Add($"\"failure\": \"{failure}\"");
        }
        return "{ " + string.Join(", ", parts) + " }";
    }

    internal static string Timing(long offsetMs = 0, double scale = 1) =>
        $$"""{ "offset_ms": {{offsetMs}}, "scale": {{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""";

    internal static string State(string key, string? job = null, string? timing = null, string source = "downloaded", string fileId = FileId) =>
        $$"""
        { "key": "{{key}}", "media_file_id": "{{fileId}}", "source": "{{source}}", "language": "de", "format": "srt",
          "label": "Elena.Release.1080p", "timing": {{timing ?? Timing()}}{{(job is null ? "" : ", \"sync\": " + job)}} }
        """;

    internal static string List(params string[] states) => $$"""{ "subtitles": [ {{string.Join(", ", states)}} ] }""";

    internal static string One(string state) => $$"""{ "subtitle": {{state}} }""";

    private static string Bool(bool value) => value ? "true" : "false";

    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private async Task FollowAsync(params string[] keys)
    {
        _tracker.Follow(ScriptedSilo.Base, FileId, keys.Length == 0 ? [Stored, External] : keys);
        await Until(() => _tracker.Entries.Count == 2, "the file's subtitles were read");
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);
        await Task.Delay(30, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheFilesSyncableSubtitles_AreRead_OnceTheServerSaysItSyncs()
    {
        await FollowAsync();

        Assert.True(_tracker.IsAvailable);
        Assert.True(_tracker.IsExternalAvailable);
        Assert.True(_tracker.EntryOf(External)!.State.IsExternal);
        Assert.Equal("Elena.Release.1080p", _tracker.EntryOf(Stored)!.State.Label);
    }

    [Theory]
    [InlineData(false, "available")]
    [InlineData(true, "not_configured")]
    public async Task SyncTheViewerMayNotUse_ReadsNothingMore(bool allowed, string state)
    {
        _silo.On(HttpMethod.Get, StatusPath, Ok(Status(allowed, state)));

        _tracker.Follow(ScriptedSilo.Base, FileId, [Stored]);
        await Until(() => _silo.SentTo(HttpMethod.Get, StatusPath).Count == 1, "the status was read");
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(_tracker.IsAvailable);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, ListPath));
    }

    [Fact]
    public async Task ASidecar_CanBeSynced_OnlyWhereTheServerSyncsSidecars()
    {
        _silo.On(HttpMethod.Get, StatusPath, Ok(Status(external: false)));

        await FollowAsync();

        Assert.True(_tracker.CanSync(_tracker.EntryOf(Stored)!));
        Assert.False(_tracker.CanSync(_tracker.EntryOf(External)!));
    }

    [Fact]
    public async Task SyncToAudio_StartsAJob_FollowsItEveryThreeSeconds_AndStopsOnceItEnded()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Post, StoredPath, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Stored, Job("j1", "pending")))));
        _silo.On(HttpMethod.Get, StoredPath,
            Versioned(One(State(Stored, Job("j1", "running", 0.4, "analyzing"))), "\"v1\""),
            Versioned(One(State(Stored, Job("j1", "synced", result: Timing(-3000)), Timing(-3000))), "\"v2\""));

        await _tracker.RequestSyncAsync(Stored);

        Assert.Equal("j1", _tracker.EntryOf(Stored)!.WatchedJobId);
        Assert.True(_tracker.EntryOf(Stored)!.IsInProgress);
        await AdvanceAsync(SubtitleSyncTracker.PollInterval);
        await Until(() => _tracker.EntryOf(Stored)!.Job!.Status == "running", "the running job was read");
        Assert.Equal(0.4, _tracker.EntryOf(Stored)!.Job!.Progress);
        await AdvanceAsync(SubtitleSyncTracker.PollInterval);
        await Until(() => _tracker.EntryOf(Stored)!.Job!.Status == "synced", "the job ended");
        Assert.Equal(-3000, _tracker.EntryOf(Stored)!.State.Timing.OffsetMs);
        Assert.Equal([Stored], _timingChanges);

        await AdvanceAsync(SubtitleSyncTracker.PollInterval * 3);
        Assert.Equal(2, _silo.SentTo(HttpMethod.Get, StoredPath).Count);
    }

    [Fact]
    public async Task PollingGivesUp_AfterFiveMinutes_AndOpeningTheMenuStartsItAgain()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Post, StoredPath, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Stored, Job("j1", "running", 0.1)))));
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, Job("j1", "running", 0.1))), "\"v1\""));
        await _tracker.RequestSyncAsync(Stored);

        for (int tick = 0; tick <= 101; tick++)
        {
            _time.Advance(SubtitleSyncTracker.PollInterval);
        }
        await Until(() => _tracker.EntryOf(Stored)!.PollExpired, "polling gave up");
        int reads = _silo.SentTo(HttpMethod.Get, StoredPath).Count;
        await AdvanceAsync(SubtitleSyncTracker.PollInterval * 2);
        Assert.Equal(reads, _silo.SentTo(HttpMethod.Get, StoredPath).Count);
        _silo.On(HttpMethod.Get, ListPath, Ok(List(State(Stored, Job("j1", "running", 0.2)), State(External, source: "external"))));

        _tracker.Reload();

        Assert.False(_tracker.EntryOf(Stored)!.PollExpired);
        await AdvanceAsync(SubtitleSyncTracker.PollInterval);
        await Until(() => _silo.SentTo(HttpMethod.Get, StoredPath).Count > reads, "polling started again");
    }

    [Fact]
    public async Task ResetTiming_WritesTheOriginalTiming_AtTheVersionJustRead()
    {
        _silo.On(HttpMethod.Get, ListPath, Ok(List(State(Stored, Job("j1", "synced", result: Timing(2500)), Timing(2500)), State(External, source: "external"))));
        await FollowAsync();
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, Job("j1", "synced", result: Timing(2500)), Timing(2500))), "\"v7\""));
        _silo.On(HttpMethod.Put, TimingPath, Versioned(One(State(Stored, Job("j1", "synced", result: Timing(2500)))), "\"v8\""));

        await _tracker.ResetTimingAsync(Stored);

        var put = Assert.Single(_silo.SentTo(HttpMethod.Put, TimingPath));
        Assert.Equal("\"v7\"", put.IfMatch);
        var body = JsonDocument.Parse(put.Body!).RootElement;
        Assert.Equal(0, body.GetProperty("offset_ms").GetInt64());
        Assert.Equal(1, body.GetProperty("scale").GetDouble());
        Assert.True(_tracker.EntryOf(Stored)!.State.Timing.IsIdentity);
        Assert.False(_tracker.EntryOf(Stored)!.IsBusy);
        Assert.Equal([Stored], _timingChanges);
    }

    [Theory]
    [InlineData(403, true, false, null)]
    [InlineData(422, false, true, "This format can't be synced.")]
    [InlineData(412, false, false, "The subtitle changed. Try again.")]
    [InlineData(500, false, false, "Sync failed")]
    public async Task ARefusedSync_IsWordedAsTheWebWordsIt(int status, bool forbidden, bool unsupported, string? error)
    {
        await FollowAsync();
        _silo.On(HttpMethod.Post, StoredPath, ScriptedSilo.Problem(status, "refused"));

        await _tracker.RequestSyncAsync(Stored);

        var entry = _tracker.EntryOf(Stored)!;
        Assert.False(entry.IsBusy);
        Assert.Equal(forbidden, entry.IsForbidden);
        Assert.Equal(unsupported, entry.IsUnsupported);
        Assert.Equal(error, entry.Error);
        Assert.Equal(!unsupported, _tracker.CanSync(entry));
    }

    [Fact]
    public async Task AResetRefusedAsStale_ReadsTheSubtitleAgain()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Get, StoredPath,
            Versioned(One(State(Stored, timing: Timing(900))), "\"v1\""),
            Versioned(One(State(Stored, timing: Timing(1200))), "\"v2\""));
        _silo.On(HttpMethod.Put, TimingPath, ScriptedSilo.Problem(412, "precondition-failed"));

        await _tracker.ResetTimingAsync(Stored);

        await Until(() => _tracker.EntryOf(Stored)!.State.Timing.OffsetMs == 1200, "the subtitle was read again");
        Assert.Equal("The subtitle changed. Try again.", _tracker.EntryOf(Stored)!.Error);
        Assert.False(_tracker.EntryOf(Stored)!.IsBusy);
    }

    [Fact]
    public async Task AFailedReadAfterATimingEvent_DoesNotHideTheNextChange()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Get, StoredPath, ScriptedSilo.Problem(500, "internal"));
        _tracker.OnTimingChanged(Stored);
        await Until(() => _silo.SentTo(HttpMethod.Get, StoredPath).Count == 1, "the read after the event");
        await Task.Delay(30, TestContext.Current.CancellationToken);

        var running = new SubtitleSyncJob { Id = "j2", Status = "synced", Trigger = "manual", CreatedAt = "2026-10-29T11:00:00.000Z" };
        _tracker.OnSyncUpdated(Stored, new SubtitleTiming { OffsetMs = 700, RawScale = 1 }, running);

        Assert.Equal([Stored, Stored], _timingChanges);
    }

    [Fact]
    public async Task ARequestThatHangs_FreesItsButtons_AfterItsTimeLimit()
    {
        var hung = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                using var registration = cancellationToken.Register(() => hung.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            string json = path.EndsWith("/status", StringComparison.Ordinal) ? Status() : List(State(Stored));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var tracker = new SubtitleSyncTracker(new SubtitleSyncApi(new Granary.Core.Api.SiloHttp(new HttpClient(handler))), new ImmediateDispatcher(),
            _time, new LogSink().For<SubtitleSyncTracker>());
        tracker.Follow(ScriptedSilo.Base, FileId, [Stored]);
        await Until(() => tracker.EntryOf(Stored) is not null, "the subtitle was read");

        var sync = tracker.RequestSyncAsync(Stored);
        Assert.True(tracker.EntryOf(Stored)!.IsBusy);
        _time.Advance(SubtitleSyncTracker.ActionTimeout);
        await sync;

        Assert.True(await hung.Task);
        Assert.False(tracker.EntryOf(Stored)!.IsBusy);
        Assert.Equal("Sync failed", tracker.EntryOf(Stored)!.Error);
    }

    [Fact]
    public async Task ARefusalThatArrivesAfterAnotherFile_IsDropped_WithoutAnError()
    {
        await FollowAsync();
        var gate = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, StoredPath, After(gate.Task, ScriptedSilo.Problem(500, "internal")));
        _silo.On(HttpMethod.Get, "api/v2/subtitles/42/sync", Ok(List()));
        var sync = _tracker.RequestSyncAsync(Stored);

        _tracker.Follow(ScriptedSilo.Base, "42", []);
        gate.SetResult();

        await sync;
        Assert.Empty(_tracker.Entries);
    }

    [Fact]
    public async Task PollingGivesUp_FiveMinutesAfterItBegan()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Post, StoredPath, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Stored, Job("j1", "running", 0.1)))));
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, Job("j1", "running", 0.1))), "\"v1\""));
        await _tracker.RequestSyncAsync(Stored);

        for (int tick = 0; tick < 100; tick++)
        {
            _time.Advance(SubtitleSyncTracker.PollInterval);
        }

        await Until(() => _tracker.EntryOf(Stored)!.PollExpired, "polling gave up at five minutes");
    }

    [Fact]
    public async Task ASubtitleTheServerNoLongerHas_IsForgotten_WithItsRunningJob()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Post, StoredPath, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Stored, Job("j1", "pending")))));
        _silo.On(HttpMethod.Get, StoredPath, ScriptedSilo.Problem(404, "not-found"));
        await _tracker.RequestSyncAsync(Stored);

        await AdvanceAsync(SubtitleSyncTracker.PollInterval);

        await Until(() => _tracker.EntryOf(Stored) is null, "the subtitle was forgotten");
        Assert.NotNull(_tracker.EntryOf(External));
    }

    [Fact]
    public async Task AnotherFile_DropsEverything_AndAnAnswerForTheOldOneIsIgnored()
    {
        await FollowAsync();
        var gate = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, StoredPath, After(gate.Task, ScriptedSilo.Json(HttpStatusCode.Accepted, One(State(Stored, Job("j1", "pending"))))));
        _silo.On(HttpMethod.Get, "api/v2/subtitles/42/sync", Ok(List()));
        var sync = _tracker.RequestSyncAsync(Stored);

        _tracker.Follow(ScriptedSilo.Base, "42", []);
        gate.SetResult();
        await sync;

        Assert.Empty(_tracker.Entries);
        Assert.Equal("42", _tracker.FileId);
    }

    [Fact]
    public async Task AnEventFromTheLane_MovesTheJobOn_AndAnOlderStageOfItIsIgnored()
    {
        await FollowAsync();
        var running = JsonDocument.Parse(Job("j1", "running", 0.5, "matching")).RootElement.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob)!;
        var pending = JsonDocument.Parse(Job("j1", "pending")).RootElement.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob)!;

        _tracker.OnSyncUpdated(Stored, SubtitleTiming.Identity, running);
        _tracker.OnSyncUpdated(Stored, SubtitleTiming.Identity, pending);

        Assert.Equal("running", _tracker.EntryOf(Stored)!.Job!.Status);
        Assert.Equal("matching", _tracker.EntryOf(Stored)!.Job!.Phase);
    }

    [Fact]
    public async Task ARecentEvent_SparesThatSubtitlesPoll()
    {
        await FollowAsync();
        var running = JsonDocument.Parse(Job("j1", "running", 0.5)).RootElement.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob)!;
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, Job("j1", "running", 0.6))), "\"v1\""));

        _tracker.OnSyncUpdated(Stored, SubtitleTiming.Identity, running);
        await AdvanceAsync(SubtitleSyncTracker.PollInterval);

        Assert.Empty(_silo.SentTo(HttpMethod.Get, StoredPath));
        await AdvanceAsync(SubtitleSyncTracker.PollInterval);
        await Until(() => _silo.SentTo(HttpMethod.Get, StoredPath).Count == 1, "the poll after the event went stale");
    }

    [Fact]
    public async Task ATimingEvent_RightAfterAPushedResult_ReloadsTheTrackOnce()
    {
        await FollowAsync();
        var synced = JsonDocument.Parse(Job("j1", "synced", result: Timing(-1200))).RootElement.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob)!;
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, Job("j1", "synced", result: Timing(-1200)), Timing(-1200))), "\"v1\""));

        _tracker.OnSyncUpdated(Stored, new SubtitleTiming { OffsetMs = -1200, RawScale = 1 }, synced);
        _tracker.OnTimingChanged(Stored);
        await Until(() => _silo.SentTo(HttpMethod.Get, StoredPath).Count == 1, "the subtitle was read again");
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.Equal([Stored], _timingChanges);
    }

    [Fact]
    public async Task ATimingEvent_ReloadsTheTrack_AndTheReadAfterItDoesNotReloadItAgain()
    {
        await FollowAsync();
        _silo.On(HttpMethod.Get, StoredPath, Versioned(One(State(Stored, timing: Timing(800))), "\"v1\""));

        _tracker.OnTimingChanged(Stored);
        await Until(() => _tracker.EntryOf(Stored)!.State.Timing.OffsetMs == 800, "the new timing was read");

        Assert.Equal([Stored], _timingChanges);
    }

    [Fact]
    public async Task ASubtitleTheViewerAdded_IsFollowed_WhenItIsOfThisFile()
    {
        await FollowAsync();
        var added = new StoredSubtitle
        {
            Id = "13", MediaFileId = FileId, Provider = "opensubtitles", Language = "en", Format = "srt",
            Sync = JsonDocument.Parse(Job("j9", "pending", trigger: "auto")).RootElement.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob),
        };

        _tracker.Remember(added);
        _tracker.Remember(added with { Id = "14", MediaFileId = "99" });

        Assert.Equal("j9", _tracker.EntryOf("stored-13")!.WatchedJobId);
        Assert.Equal("opensubtitles", _tracker.EntryOf("stored-13")!.State.Label);
        Assert.Null(_tracker.EntryOf("stored-14"));
    }

    [Fact]
    public async Task TheList_IsReadAgain_WhenTheInventoryGainsASubtitle()
    {
        await FollowAsync();

        _tracker.Follow(ScriptedSilo.Base, FileId, [Stored, External]);
        _tracker.Follow(ScriptedSilo.Base, FileId, [External, Stored, "stored-13"]);

        await Until(() => _silo.SentTo(HttpMethod.Get, ListPath).Count == 2, "the list was read again");
        Assert.Single(_silo.SentTo(HttpMethod.Get, StatusPath));
    }

}
