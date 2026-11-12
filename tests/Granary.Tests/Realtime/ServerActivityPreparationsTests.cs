using System.Net;
using Granary.Core.Api;
using Granary.Core.Realtime;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The queue that prepares offline downloads, as the web keeps it: read once the server gave its channel and on its
/// events, a progress reading patched into its running job, read again every minute while jobs are on their way, and
/// simply absent on a server without such a queue (503).
/// </summary>
public sealed class ServerActivityPreparationsTests : IAsyncDisposable
{
    private const string QueuePath = "api/v2/admin/downloads/preparations";

    private const string Queue = """
        {
          "counts": { "running": 1, "queued": 2, "retrying": 0, "paused": 1, "failed_recent": 1 },
          "items": [
            { "id": "job-1", "state": "running", "format": "transcode", "log_session_id": "l1", "media_file_id": "48211",
              "content_id": "series:dark", "media_title": "Past and Present", "media_type": "episode", "series_name": "Dark",
              "episode_name": "Past and Present", "season_number": 1, "episode_number": 3,
              "source": { "container": "mkv", "video_codec": "hevc", "resolution": "2160p", "hdr": true, "audio_tracks": [], "file_size": 9, "duration_seconds": 3000 },
              "output": { "container": "mp4", "video_codec": "h264", "audio_codec": "aac", "resolution": "1080p", "all_audio_tracks": false },
              "worker": { "kind": "node", "node_id": "3", "name": "gpu-1" }, "attempts": 1, "max_attempts": 3,
              "created_at": "2026-10-27T12:00:00.000Z", "started_at": "2026-10-27T12:01:00.000Z",
              "progress": { "encoded_seconds": 600, "duration_seconds": 3000, "speed": 2, "updated_at": "2026-10-27T12:06:00.000Z" },
              "progress_unavailable": false, "requesters": [] },
            { "id": "job-2", "state": "queued", "format": "remux", "queue_position": 1, "media_file_id": "12", "media_title": "Dune",
              "media_type": "movie", "source": { "hdr": false, "audio_tracks": [], "file_size": 1, "duration_seconds": 1 },
              "output": { "container": "mp4", "video_codec": "copy", "audio_codec": "copy", "all_audio_tracks": true },
              "attempts": 0, "max_attempts": 3, "created_at": "2026-10-27T12:02:00.000Z", "progress_unavailable": false, "requesters": [] }
          ]
        }
        """;

    private const string Idle = """{ "counts": { "running": 0, "queued": 0, "retrying": 0, "paused": 0, "failed_recent": 0 }, "items": [] }""";

    private const string Changed = """{ "type": "event", "channel": "download_preparations", "event": "download_preparation.changed", "event_id": "c1", "timestamp": "t", "data": { "id": "job-1" } }""";

    private readonly ServerActivityHarness _harness = new();

    public ServerActivityPreparationsTests()
    {
        _harness.Library.Answer(QueuePath, Queue);
    }

    private int Reads => _harness.Library.Count(QueuePath);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static string Progress(string id, double encoded) =>
        $$"""{ "type": "event", "channel": "download_preparations", "event": "download_preparation.progress", "event_id": "p", "timestamp": "t", "data": { "id": "{{id}}", "progress": { "encoded_seconds": {{encoded.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "duration_seconds": 3000, "speed": 2.5, "updated_at": "2026-10-27T12:07:00.000Z" } } }""";

    private async Task<Fakes.FakeRealtimeSocket> ReadAsync()
    {
        var socket = await _harness.StartedAsync(EventsChannels.DownloadPreparations);
        await ServerPlaybackFixture.Until(() => _harness.State.Preparations is not null, "the queue");
        return socket;
    }

    [Fact]
    public async Task TheQueue_IsReadOnceTheServerGaveItsChannel()
    {
        await ReadAsync();

        var queue = _harness.State.Preparations!;
        Assert.Equal((1, 2, 0, 1, 1, 3), (queue.Counts.Running, queue.Counts.Queued, queue.Counts.Retrying, queue.Counts.Paused,
            queue.Counts.FailedRecent, queue.Counts.Active));
        var job = queue.Items[0];
        Assert.Equal(("job-1", true, "transcode", "48211", "Dark", 1, 3), (job.Id, job.IsRunning, job.Format, job.MediaFileId, job.SeriesName,
            job.SeasonNumber, job.EpisodeNumber));
        Assert.Equal(("h264", "1080p", "node", "3", "gpu-1"), (job.Output!.VideoCodec, job.Output.Resolution, job.Worker!.Kind, job.Worker.NodeId, job.Worker.Name));
        Assert.Equal((600d, 3000d, 2d), (job.Progress!.EncodedSeconds, job.Progress.DurationSeconds, job.Progress.Speed));
        Assert.Null(queue.Items[1].Worker);
        Assert.Null(queue.Items[1].Progress);
        Assert.Equal("limit=200", Assert.Single(_harness.Library.Queries(QueuePath)));
    }

    [Fact]
    public async Task AProgressReading_IsPutIntoItsRunningJob_WithoutARead()
    {
        var socket = await ReadAsync();
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        await _harness.PushedAsync(socket, Progress("job-1", 1500));

        Assert.Equal((1500d, 2.5), (_harness.State.Preparations!.Items[0].Progress!.EncodedSeconds, _harness.State.Preparations.Items[0].Progress!.Speed));
        Assert.Equal("Dark", _harness.State.Preparations.Items[0].SeriesName);
        Assert.Equal(1, Reads);
    }

    [Theory]
    [InlineData("job-2")]
    [InlineData("job-9")]
    public async Task AReadingForAJobThatDoesNotRunInTheList_ReadsTheQueueAgain(string id)
    {
        var socket = await ReadAsync();
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        await _harness.PushedAsync(socket, Progress(id, 10));

        await ServerPlaybackFixture.Until(() => Reads == 2, "the read");
    }

    [Fact]
    public async Task AnyOtherNewsOfTheQueue_ReadsItAgain_ButNewsOfTheStorageDoesNot()
    {
        var socket = await ReadAsync();
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        await _harness.PushedAsync(socket, """{ "type": "event", "channel": "download_preparations", "event": "download_storage.changed", "event_id": "s1", "timestamp": "t", "data": {} }""");
        await _harness.PushedAsync(socket, """{ "type": "snapshot", "channel": "download_preparations", "timestamp": "t", "data": null }""");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, Reads);

        _harness.Library.Answer(QueuePath, Idle);
        await _harness.PushedAsync(socket, Changed);

        await ServerPlaybackFixture.Until(() => _harness.State.Preparations?.Items.Count == 0, "the read");
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task WhileJobsAreOnTheirWay_TheQueueIsReadAgainEveryMinute_AndNoLongerOnceTheyAreDone()
    {
        await ReadAsync();

        _harness.Library.Clock.Advance(ServerActivity.PreparationsRefresh - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Reads);
        _harness.Library.Answer(QueuePath, Idle);
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));

        await ServerPlaybackFixture.Until(() => _harness.State.Preparations?.Counts.Active == 0, "the read after a minute");
        Assert.Equal(2, Reads);
        // Nothing is on its way any more: no further read by the clock.
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the window after the read");
        _harness.Library.Clock.Advance(ServerActivity.PreparationsRefresh + ServerActivity.PreparationsRefresh);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task AServerWithoutSuchAQueue_HasNone_AndIsNotAskedAgain()
    {
        _harness.Library.Answer(QueuePath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable, TimeSpan.FromSeconds(30)));
        var socket = await _harness.StartedAsync(EventsChannels.DownloadPreparations);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read");

        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await _harness.PushedAsync(socket, Changed);
        await _harness.PushedAsync(socket, Progress("job-1", 10));
        _harness.Library.Clock.Advance(ServerActivity.PreparationsRefresh + ServerActivity.PreparationsRefresh);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, Reads);
        Assert.Null(_harness.State.Preparations);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AnotherProfile_StopsTheMinutesRead()
    {
        await ReadAsync();

        _harness.Library.Session.ProfileId = "8";
        _harness.Library.Session.RaiseChanged();
        await _harness.OpenedAsync(1);
        _harness.Library.Clock.Advance(ServerActivity.PreparationsRefresh + ServerActivity.PreparationsRefresh);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, Reads);
        Assert.Null(_harness.State.Preparations);
    }

    [Fact]
    public async Task AllFourSources_AreFollowedSideBySide()
    {
        _harness.Library.Answer("api/v2/admin/sessions", Admin.AdminPayloads.Sessions);
        _harness.Library.Silo.On(HttpMethod.Get, "api/v2/admin/tasks", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "items": [ { "key": "backup", "name": "Back Up Server", "state": "running", "progress": 0 } ] }"""));
        _harness.Library.Answer("api/v2/libraries", Admin.AdminPayloads.Libraries);

        var socket = await _harness.StartedAsync([.. EventsChannels.Admin]);
        await _harness.PushedAsync(socket, """{ "type": "snapshot", "channel": "scans", "timestamp": "t", "data": [ { "id": "a", "library_id": 1, "mode": "library", "trigger": "manual", "status": "running" } ] }""");

        await ServerPlaybackFixture.Until(() => _harness.State is { Sessions.Count: 2, Tasks.Count: 1, Scans.Count: 1, Preparations: not null, LibraryNames: not null },
            "every source");
    }
}
