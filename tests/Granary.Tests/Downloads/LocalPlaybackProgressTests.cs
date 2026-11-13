using System.Net;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>Where the viewer is in a download: recorded on this PC, sent to the server, and where the next play starts.</summary>
public sealed class LocalPlaybackProgressTests : IAsyncDisposable
{
    private readonly LocalPlaybackFixture _fixture = new();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private void Frame(double seconds, bool paused = false) => _fixture.Engine.RaiseState(LocalPlaybackFixture.Frame(seconds, paused));

    private OfflineProgressEntry Entry => Assert.Single(_fixture.Queue.Entries(Scope7));

    private DownloadRecord Record => _fixture.Harness.Find("dl_1")!.Record;

    [Fact]
    public async Task ALocalPlay_RecordsEvery10s_AndAtTheStop()
    {
        // Nothing leaves the queue here: what was recorded stays to be looked at.
        _fixture.Silo.On(HttpMethod.Post, LocalPlaybackFixture.SyncPath, ScriptedSilo.NetworkError());
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(9.5);

        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);

        Assert.Equal(("ep_5", 9_500L, 7_200_000L, ManualTimeProvider.DefaultStart.AddSeconds(10)),
            (Entry.MediaItemId, Entry.PositionMs, Entry.DurationMs, Entry.UpdatedAt));

        Frame(42);
        _fixture.Harness.Time.Advance(TimeSpan.FromSeconds(3));
        await playback.StopAsync();

        Assert.Equal((42_000L, ManualTimeProvider.DefaultStart.AddSeconds(13)), (Entry.PositionMs, Entry.UpdatedAt));
        // The download itself keeps it too: the next play from this PC starts there, also without the server.
        Assert.Equal((42_000L, ManualTimeProvider.DefaultStart.AddSeconds(13), false), (Record.ResumeMs, Record.ResumeAt, Record.IsWatched));
    }

    [Fact]
    public async Task NothingIsRecorded_BeforeTheFirstFrame()
    {
        await _fixture.Harness.AddPlayableAsync();
        var playback = await _fixture.StartAsync();

        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);
        await playback!.StopAsync();

        // Opening a title and leaving at once moves its resume point nowhere.
        Assert.Empty(_fixture.Queue.Entries(Scope7));
        Assert.Null(Record.ResumeMs);
    }

    [Fact]
    public async Task ATitleLeftPaused_DoesNotGrowNewerByItself()
    {
        _fixture.Silo.On(HttpMethod.Post, LocalPlaybackFixture.SyncPath, ScriptedSilo.NetworkError());
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(60, paused: true);
        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);
        var recorded = Entry;

        // An hour paused: watched on from another device meanwhile, that position is the newer one.
        _fixture.Harness.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal((recorded.PositionMs, recorded.UpdatedAt), (Entry.PositionMs, Entry.UpdatedAt));
    }

    [Fact]
    public async Task EachPosition_GoesToTheServer_WithoutAPlaybackSession()
    {
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(9.5);
        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);

        await _fixture.FlushedAsync();

        var sent = Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, LocalPlaybackFixture.SyncPath));
        Assert.Contains("\"media_item_id\":\"ep_5\",\"position_ms\":9500,\"duration_ms\":7200000", sent.Body, StringComparison.Ordinal);
        Assert.Equal("7", sent.AsProfile);
        Assert.Empty(_fixture.Queue.Entries(Scope7));
        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/playback/", StringComparison.Ordinal));
        // A position on the way asks for nothing back; the stop does.
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, LocalPlaybackFixture.ProgressPath));
    }

    [Fact]
    public async Task TheStop_AsksTheServerWhetherTheTitleIsWatchedNow()
    {
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(3000);
        // The server's own threshold decides: it counts the title as watched and sets its position back.
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "media_item_id": "ep_5", "position_seconds": 0, "duration_seconds": 7200, "completed": true, "updated_at": "2026-10-01T12:00:00Z" } ],
              "page": { "has_more": false } }
            """));

        await playback.StopAsync();
        await _fixture.FlushedAsync();

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, LocalPlaybackFixture.ProgressPath));
        Assert.Equal((0L, true), (Record.ResumeMs, Record.IsWatched));
    }

    [Fact]
    public async Task TheEndOfTheFile_CountsAsWatched_AndTheNextPlayStartsOver()
    {
        _fixture.GoOffline();
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(7195);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await _fixture.Queue.LatestSave;
        await Until(() => Record.IsWatched, "the download is marked watched");

        // The whole length goes to the server, which decides by its own threshold.
        Assert.Equal(7_200_000, Entry.PositionMs);
        Assert.Equal(0, Record.ResumeMs);
        await _fixture.StartAsync();
        Assert.Equal(TimeSpan.Zero, _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task AStopPastNineTenths_StartsOverNextTime_ButIsNotWatchedUntilTheServerSaysSo()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(6600);
        await playback.StopAsync();

        // The server's threshold is an admin's setting: nine tenths here is a guess, good for where to start, not for
        // suggesting the download's deletion.
        Assert.Equal((6_600_000L, 0L, false), (Entry.PositionMs, Record.ResumeMs, Record.IsWatched));
    }

    [Fact]
    public async Task AWatchedTitleThatIsWatchedAgain_IsNotWatched_WhileThatWatchIsUnderWay()
    {
        _fixture.GoOffline();
        await _fixture.Harness.AddPlayableAsync(change: r => r with { IsWatched = true });
        var again = await _fixture.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);
        Frame(300);
        await again!.StopAsync();

        // In the middle of a rewatch nobody wants it suggested for deletion; it resumes where the rewatch got to.
        Assert.Equal((300_000L, false), (Record.ResumeMs, Record.IsWatched));
    }

    [Fact]
    public async Task APositionWatchedNow_IsLaterThanWhatTheDownloadAlreadyKnew_AlsoOnAClockThatIsBehind()
    {
        _fixture.GoOffline();
        // The download holds another device's position from 14:00, read from the server; this PC's clock says 12:00.
        var known = ManualTimeProvider.DefaultStart.AddHours(2);
        await _fixture.Harness.AddPlayableAsync(change: r => r with { ResumeMs = 3_000_000, ResumeAt = known });
        var playback = await _fixture.StartAsync(PlayRequest.FromStart("ep_5"));
        Frame(0);
        Frame(120);

        await playback!.StopAsync();

        // The viewer is at two minutes now, whatever the clock reads: stamped after what was known, so the server takes it
        // and the next play from this PC starts there.
        Assert.Equal((120_000L, known.AddSeconds(1)), (Entry.PositionMs, Entry.UpdatedAt));
        Assert.Equal((120_000L, known.AddSeconds(1)), (Record.ResumeMs, Record.ResumeAt));
    }

    [Fact]
    public async Task APositionTheServerHasNotTaken_IsWhereTheNextPlayStarts_WhateverTheServerSays()
    {
        _fixture.Silo.On(HttpMethod.Post, LocalPlaybackFixture.SyncPath, ScriptedSilo.NetworkError());
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(600);
        await playback.StopAsync();
        await _fixture.FlushedAsync();
        Assert.Equal(OfflineProgressState.Pending, Entry.State);

        // The server still names the older point (30 min 30.5 s); this PC's is the newer one.
        await _fixture.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(600), _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task OnceTheServerTookThePosition_ItsOwnPointIsWhereTheNextPlayStarts()
    {
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(600);
        await playback.StopAsync();
        await _fixture.FlushedAsync();
        Assert.Empty(_fixture.Queue.Entries(Scope7));
        // Watched on to 50 min on another device since.
        _fixture.Silo.On(HttpMethod.Get, LocalPlaybackFixture.WatchPath, ScriptedSilo.Json(HttpStatusCode.OK, LocalPlaybackFixture.Watch(3000)));

        await _fixture.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(3000), _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task AFileThatBreaksOff_KeepsWhereTheViewerWas()
    {
        _fixture.GoOffline();
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(1500);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "read error"));

        Assert.Equal(1_500_000, Entry.PositionMs);
        // An end long before the title's is no end: not watched.
        await Until(() => Record.ResumeMs == 1_500_000, "the download kept the position");
        Assert.False(Record.IsWatched);
    }

    [Fact]
    public async Task AFileThatBreaksOffPastNineTenths_IsNeitherWatchedNorStartedOver_AndRetryGoesOnFromThere()
    {
        _fixture.GoOffline();
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(6600);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "read error"));

        // Left there by choice, the title would start over next time. It broke off: the last ten minutes are still to watch.
        await Until(() => Record.ResumeMs == 6_600_000, "the download kept the position");
        Assert.False(Record.IsWatched);
        Assert.Equal(6_600_000, Entry.PositionMs);

        // Retry is a new play of the title, from where it left off.
        await _fixture.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(6600), _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task AFileThatBreaksOffWhereItWasLastRecordedPastNineTenths_StillGoesOnFromThere()
    {
        _fixture.GoOffline();
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(6600);
        // Recorded on the way, as any position past nine tenths: the title would start over.
        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);
        await Until(() => Record.ResumeMs == 0, "the download kept the position on the way");

        // The file breaks off before the position moved on.
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "read error"));

        await Until(() => Record.ResumeMs == 6_600_000, "the download kept where it broke off");
        Assert.False(Record.IsWatched);
        Assert.Equal(6_600_000, Entry.PositionMs);
    }

    [Fact]
    public async Task AFileThatBreaksOffWhereItWasLastRecorded_IsNotRecordedAgain()
    {
        _fixture.GoOffline();
        await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(1500, paused: true);
        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);
        var recorded = Entry;

        // Paused for an hour, then the file would not play on: the position is the one known already, and grows no newer.
        _fixture.Harness.Time.Advance(TimeSpan.FromHours(1));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "read error"));

        Assert.Equal((recorded.PositionMs, recorded.UpdatedAt), (Entry.PositionMs, Entry.UpdatedAt));
    }

    [Fact]
    public async Task AFileThatEndsLongBeforeItsLength_IsNotTheTitlesEnd()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(1500);

        // The player reached the end of the data, 95 minutes before the title's.
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        // Not ended: nothing plays next, and the title is not watched.
        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, LocalPlayback.CannotPlay, CanRetry: true), playback.Status);
        Assert.Equal(1_500_000, Entry.PositionMs);
        await Until(() => Record.ResumeMs == 1_500_000, "the download kept the position");
        Assert.False(Record.IsWatched);
    }

    [Fact]
    public async Task AFileShorterThanTheTitleItsManifestNames_IsNotTheTitlesEnd_ThoughItPlaysThrough()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        // The file in the download's place is an hour long, by its own word; the manifest names a title of two.
        _fixture.Engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(3595), TimeSpan.FromSeconds(3600), null, HasStarted: true));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        // Not the title's end: it is not watched, nothing plays next, and where the viewer got to is kept.
        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, LocalPlayback.CannotPlay, CanRetry: true), playback.Status);
        await Until(() => Record.ResumeMs == 3_595_000, "the download kept the position");
        Assert.False(Record.IsWatched);
    }

    [Fact]
    public async Task AFileALittleLongerOrShorterThanItsManifestSays_EndsAsTheTitle()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        // A copy the server prepared is seconds off the source's length the manifest names.
        _fixture.Engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(7188), TimeSpan.FromSeconds(7189), null, HasStarted: true));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        Assert.Equal(ServerPlaybackPhase.Ended, playback.Status.Phase);
        await Until(() => Record.IsWatched, "the download is marked watched");
    }

    [Fact]
    public async Task ADownloadDeletedWhileItPlays_StillStopsCleanly()
    {
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(120);
        await _fixture.Harness.Store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, LocalPlaybackFixture.Token);

        await playback.StopAsync();

        // The server still hears where the viewer was, at the stop.
        var sent = Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, LocalPlaybackFixture.SyncPath));
        Assert.Contains("\"position_ms\":120000", sent.Body, StringComparison.Ordinal);
        Assert.Empty(_fixture.Queue.Entries(Scope7));
    }

    [Fact]
    public async Task TheStop_DoesNotWaitLongerThanItsBudget_ForAServerThatDoesNotAnswer()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, LocalPlaybackFixture.SyncPath, () => answer.Task);
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(120);

        var stop = playback.StopAsync();
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, LocalPlaybackFixture.SyncPath).Count == 1, "the last position left");
        Assert.False(stop.IsCompleted);
        _fixture.Harness.Time.Advance(LocalPlayback.StopSendBudget);
        await stop.WaitAsync(TimeSpan.FromSeconds(5), LocalPlaybackFixture.Token);

        // The position is kept and the send goes on by itself.
        Assert.Equal(OfflineProgressState.Sending, Entry.State);
        answer.SetException(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));
        await _fixture.Sync.LatestRun;
        Assert.Equal(OfflineProgressState.Pending, Entry.State);
    }

    [Fact]
    public async Task APlayStillOpenWhenItsAccountIsSignedOutWithItsDownloads_RecordsNothingMore()
    {
        _fixture.GoOffline();
        var playback = await _fixture.PlayingAsync(PlayRequest.FromStart("ep_5"));
        Frame(120);
        _fixture.Harness.Time.Advance(LocalPlayback.ProgressInterval);
        Assert.Equal(120_000, Entry.PositionMs);

        // The sign-out marks the account's downloads as going and takes what waited to be sent; then the player closes.
        long clear = _fixture.Harness.Store.MarkCleared("srv1", "1");
        _fixture.Queue.Remove("srv1", "1");
        Frame(300);
        await playback.StopAsync();
        _fixture.Harness.Store.EndClear(clear);

        Assert.Empty(_fixture.Queue.Entries(Scope7));
    }
}
