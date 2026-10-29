using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;
using static Granary.Tests.Playback.SubtitleSyncTrackerTests;

namespace Granary.Tests.Playback;

/// <summary>
/// A subtitle whose timing changed shows its new timing at once, as Silo's web player reloads its cues, and the control
/// lane's sync events reach the sync state.
/// </summary>
public sealed class ServerPlaybackSubtitleSyncTests
{
    private const string Key = "external-f903db1c5624c46c4a2c20f2fd8cd247fc0d46e6471c91d4ddb651ae4e039448";
    private const string Track = "file:42:subtitle:0";
    private const string ListPath = "api/v2/subtitles/42/sync";
    private const string KeyPath = ListPath + "/" + Key;
    private const string TicketPath = "api/v2/playback/sessions/" + Session + "/control/ws-ticket";

    private readonly ServerPlaybackFixture _fixture = new() { SyncsSubtitles = true };
    private readonly List<string> _timingChanges = [];
    private readonly List<string> _reloads = [];

    public ServerPlaybackSubtitleSyncTests()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/subtitles/sync/status", Ok(Status()));
        _fixture.Silo.On(HttpMethod.Get, ListPath, Ok(List(State(Key, source: "external", fileId: "42"))));
        _fixture.Silo.On(HttpMethod.Get, KeyPath, Versioned(One(State(Key, timing: Timing(1500), source: "external", fileId: "42"))));
    }

    private static Func<Task<HttpResponseMessage>> Versioned(string json) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
        return Task.FromResult(response);
    };

    private async Task<ServerPlayback> ShowingTheSidecarAsync()
    {
        var playback = await _fixture.PlayingAsync();
        playback.SubtitleTimingChanged += (_, key) => _timingChanges.Add(key);
        playback.SubtitleCuesReloaded += (_, key) => _reloads.Add(key);
        playback.SelectSubtitle(Track);
        await Until(() => _fixture.Engine.SubtitleStreams.Contains(3), "the sidecar shows");
        await Until(() => playback.SubtitleSync!.EntryOf(Key) is not null, "its sync state was read");
        return playback;
    }

    [Fact]
    public async Task ThePlayingFilesSubtitles_AreFollowed_AndTheOneThatShowsIsNamed()
    {
        await using var playback = await ShowingTheSidecarAsync();

        Assert.Equal("42", playback.SubtitleSync!.FileId);
        Assert.Equal(Key, playback.SubtitleSyncKey);
    }

    [Fact]
    public async Task ASubtitleThatShows_IsReadAgain_WhenItsTimingChanged()
    {
        await using var playback = await ShowingTheSidecarAsync();
        _fixture.Engine.ReadSubtitle = _ => Task.FromResult<int?>(4);

        playback.SubtitleSync!.OnTimingChanged(Key);

        await Until(() => _reloads.Count == 1, "the new cues show");
        Assert.Equal([Key], _timingChanges);
        Assert.Equal(4, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Equal([3], _fixture.Engine.RemovedSubtitles);
        Assert.Equal(2, _fixture.Engine.SubtitleFiles.Count);
        Assert.NotEqual(_fixture.Engine.SubtitleFiles[0].Url, _fixture.Engine.SubtitleFiles[1].Url);
    }

    [Fact]
    public async Task ThePickChangingDuringTheReload_StillTakesTheOldTrackOut()
    {
        await using var playback = await ShowingTheSidecarAsync();
        var reread = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Engine.ReadSubtitle = _ => reread.Task;

        playback.SubtitleSync!.OnTimingChanged(Key);
        await Until(() => _fixture.Engine.SubtitleFiles.Count == 2, "the subtitle is read again");
        playback.SelectSubtitle(null);
        reread.SetResult(4);

        await Until(() => _fixture.Engine.RemovedSubtitles.Contains(3), "the old track went");
        Assert.Empty(_reloads);
        Assert.Equal(0, _fixture.Engine.SubtitleStreams[^1]);
    }

    [Fact]
    public async Task AReloadThatCannotBeRead_StillTakesTheOldTrackOut()
    {
        await using var playback = await ShowingTheSidecarAsync();
        _fixture.Engine.ReadSubtitle = _ => Task.FromResult<int?>(null);

        playback.SubtitleSync!.OnTimingChanged(Key);

        await Until(() => _fixture.Engine.RemovedSubtitles.Contains(3), "the old track went");
        Assert.Empty(_reloads);
        Assert.Null(playback.SubtitleTrack);
    }

    [Fact]
    public async Task ATimingChangeWhileTheSubtitleIsStillBeingRead_ReadsItAgainAfterward()
    {
        var playback = await _fixture.PlayingAsync();
        await using var _ = playback;
        playback.SubtitleCuesReloaded += (_, key) => _reloads.Add(key);
        await Until(() => playback.SubtitleSync!.EntryOf(Key) is not null, "the sync state was read");
        var first = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Engine.ReadSubtitle = _ => first.Task;
        playback.SelectSubtitle(Track);
        await Until(() => _fixture.Engine.SubtitleFiles.Count == 1, "the subtitle is being read");

        playback.SubtitleSync!.OnTimingChanged(Key);
        _fixture.Engine.ReadSubtitle = _ => Task.FromResult<int?>(4);
        first.SetResult(3);

        await Until(() => _reloads.Count == 1, "the new cues show");
        Assert.Equal(2, _fixture.Engine.SubtitleFiles.Count);
        Assert.Equal(4, _fixture.Engine.SubtitleStreams[^1]);
        Assert.Equal([3], _fixture.Engine.RemovedSubtitles);
    }

    [Fact]
    public async Task ASubtitleThatDoesNotShow_IsReadAgainOnlyWhenPicked()
    {
        var playback = await _fixture.PlayingAsync();
        await using var _ = playback;
        playback.SubtitleTimingChanged += (_, key) => _timingChanges.Add(key);
        await Until(() => playback.SubtitleSync!.EntryOf(Key) is not null, "the sync state was read");

        playback.SubtitleSync!.OnTimingChanged(Key);
        await Until(() => playback.SubtitleSync.EntryOf(Key)!.State.Timing.OffsetMs == 1500, "the new timing was read");

        Assert.Equal([Key], _timingChanges);
        Assert.Empty(_fixture.Engine.SubtitleFiles);
        Assert.Empty(_fixture.Engine.RemovedSubtitles);
    }

    [Fact]
    public async Task TheLanesSyncEvents_ReachTheSyncState_OfThePlayingFileOnly()
    {
        var sockets = new List<FakeRealtimeSocket>();
        _fixture.Silo.On(HttpMethod.Post, TicketPath,
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "ticket": "t0k3n", "expires_in": 30, "max_connection_seconds": 14400, "protocol": "silo.playback-control.v2" }"""));
        _fixture.ControlLane = new PlaybackControlLane(new PlaybackControlApi(_fixture.Silo.Http), () =>
        {
            var socket = new FakeRealtimeSocket();
            sockets.Add(socket);
            return socket;
        }, _fixture.Time, _fixture.Logs.For<PlaybackControlLane>());
        await using var playback = await ShowingTheSidecarAsync();
        await Until(() => sockets.Count == 1 && sockets[0].Sent.Count == 1, "the lane's hello");

        sockets[0].Push(SyncEvent("99", Job("j5", "running", 0.3)));
        sockets[0].Push(SyncEvent("42", Job("j5", "running", 0.6, "matching")));

        await Until(() => playback.SubtitleSync!.EntryOf(Key)!.Job is { Progress: 0.6 }, "the event moved the job on");
        Assert.Single(sockets[0].Sent);
    }

    private static string SyncEvent(string fileId, string job) => $$"""
        { "type": "event", "session_id": "{{Session}}", "name": "subtitle_sync_updated",
          "payload": { "session_id": "{{Session}}", "file_id": {{fileId}}, "sync_key": "{{Key}}", "timing": {{Timing()}}, "job": {{job}} } }
        """;
}
