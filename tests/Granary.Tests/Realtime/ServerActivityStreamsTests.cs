using System.Net;
using Granary.Core.Realtime;
using Granary.Tests.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The streams of the activity indicator, as the web keeps them: every page of the sessions is read once the server gave
/// their channel and again when it says they were replaced, at once and then at most once per window; the frame's own
/// rows are never used.
/// </summary>
public sealed class ServerActivityStreamsTests : IAsyncDisposable
{
    private const string SessionsPath = "api/v2/admin/sessions";
    private const string Replaced = """{ "type": "event", "channel": "sessions", "event": "sessions.replaced", "event_id": "e1", "timestamp": "t", "data": [] }""";

    private readonly ServerActivityHarness _harness = new();

    public ServerActivityStreamsTests()
    {
        _harness.Library.Answer(SessionsPath, AdminPayloads.Sessions);
    }

    private int Reads => _harness.Library.Count(SessionsPath);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private Task ShownAsync(int streams) =>
        ServerPlaybackFixture.Until(() => _harness.State.Sessions?.Count == streams, $"{streams} streams");

    [Fact]
    public async Task TheSessions_AreReadOnceTheServerGaveTheirChannel()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);

        await ShownAsync(2);
        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task WithoutTheirChannel_TheyAreNeitherReadNorShown()
    {
        await _harness.StartedAsync(EventsChannels.Scans);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(_harness.State.Sessions);
        Assert.Equal(0, Reads);
    }

    [Fact]
    public async Task SessionsReplaced_ReadsThemAgain_AtOnceAndThenOncePerWindow()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        _harness.Library.Answer(SessionsPath, AdminPayloads.NoSessions);

        // After a quiet window the first news reads at once.
        await _harness.PushedAsync(socket, Replaced);
        await ShownAsync(0);
        Assert.Equal(2, Reads);

        // More news within the window waits for its end, and makes one read however much came.
        _harness.Library.Answer(SessionsPath, AdminPayloads.Sessions);
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the window after the read");
        await _harness.PushedAsync(socket, Replaced);
        await _harness.PushedAsync(socket, Replaced);
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow - TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, Reads);

        _harness.Library.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await ShownAsync(2);
        Assert.Equal(3, Reads);

        // And nothing more without news.
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the window after the read");
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow + ServerActivity.RefreshWindow);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, Reads);
    }

    [Fact]
    public async Task AFrameTooLargeToTake_StillReadsThemAgain()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        _harness.Library.Answer(SessionsPath, AdminPayloads.NoSessions);

        await _harness.PushedAsync(socket, """{"type":"event","channel":"sessions","event":"sessions.replaced","event_id":"e2","timestamp":"t","data":[{"session_id":"s1","media_ti""");

        await ShownAsync(0);
    }

    [Fact]
    public async Task OtherNewsOfTheSessions_ReadsNothing()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        // The web follows only sessions.replaced; the snapshot's own rows are an older shape of at most 200.
        await _harness.PushedAsync(socket, """{ "type": "snapshot", "channel": "sessions", "timestamp": "t", "data": [] }""");
        await _harness.PushedAsync(socket, """{ "type": "event", "channel": "sessions", "event": "session.paused", "event_id": "e3", "timestamp": "t", "data": { "session_id": "s1" } }""");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, Reads);
        Assert.Equal(2, _harness.State.Sessions?.Count);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task AListTheServerDoesNotOffer_IsNotShown_AndNotAskedForAgainOnThisConnection(int status)
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.Problem(status, "nope"));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read");

        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, Reads);
        Assert.Null(_harness.State.Sessions);
        // The indicator itself stays: the channel was given.
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task TheNextConnection_AsksAgainForAListThatWasMissing()
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.Sessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read");

        socket.Close();
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(1), "the wait before the next connection");
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        await _harness.OpenedAsync(1, EventsChannels.Sessions);

        await ShownAsync(2);
    }

    [Fact]
    public async Task AReadThatFails_KeepsWhatWasShown_AndTheNextNewsReadsAgain()
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.Sessions), ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.NoSessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        await _harness.PushedAsync(socket, Replaced);
        await ServerPlaybackFixture.Until(() => Reads == 2 && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the failed read");
        Assert.Equal(2, _harness.State.Sessions?.Count);

        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await _harness.PushedAsync(socket, Replaced);
        await ShownAsync(0);
    }

    private int LogLines(string text) => _harness.Library.Logs.Lines.Count(line => line.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task ReadsThatKeepFailing_WaitLongerEachTime_AreWarnedOfOnce_AndSayWhenTheyWorkAgain()
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.Sessions), ScriptedSilo.NetworkError(),
            ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.NoSessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        // The stream keeps bringing news all the while. After the first failure the list waits as after any read…
        await _harness.PushedAsync(socket, Replaced);
        await ServerPlaybackFixture.Until(() => Reads == 2 && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the first failed read");
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        // …then twice as long…
        await ServerPlaybackFixture.Until(() => Reads == 3 && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow * 2, "the second failed read");
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, Reads);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        // …and twice as long again.
        await ServerPlaybackFixture.Until(() => Reads == 4 && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow * 4, "the third failed read");
        Assert.Equal(2, _harness.State.Sessions?.Count);
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow * 4);

        await ShownAsync(0);
        Assert.Equal(1, LogLines("Could not read the playback sessions"));
        Assert.Equal(2, LogLines("Still could not read the playback sessions"));
        Assert.Equal(1, LogLines("playback sessions for the activity indicator can be read again"));
        // After a read that worked the list is read at the usual pace again.
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the window after the read");
    }

    [Fact]
    public async Task ReadsThatKeepFailing_AreNeverLeftAloneForLongerThanAMinute()
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.NetworkError());
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);

        foreach (int seconds in new[] { 5, 10, 20, 40, 60, 60 })
        {
            await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(seconds), $"a wait of {seconds} s");
            await _harness.PushedAsync(socket, Replaced);
            _harness.Library.Clock.Advance(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(ServerActivity.LongestRetryWindow, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task AServerThatAsksForTime_IsGivenIt()
    {
        _harness.Library.Answer(SessionsPath, ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.Sessions),
            ScriptedSilo.Problem(429, "rate_limited", TimeSpan.FromSeconds(30)), ScriptedSilo.Json(HttpStatusCode.OK, AdminPayloads.NoSessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        await _harness.PushedAsync(socket, Replaced);
        await ServerPlaybackFixture.Until(() => Reads == 2 && _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(30), "the read turned away");
        await _harness.PushedAsync(socket, Replaced);
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, Reads);

        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        await ShownAsync(0);
    }

    [Fact]
    public async Task AListenerThatThrows_DoesNotStopTheReads_NorTheListenersAfterIt()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        int later = 0;
        _harness.Activity.Changed += (_, _) => throw new InvalidOperationException("A listener failed.");
        _harness.Activity.Changed += (_, _) => Interlocked.Increment(ref later);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        _harness.Library.Answer(SessionsPath, AdminPayloads.NoSessions);

        // The answer comes on a thread of its own, where nobody would catch what the listener throws.
        await _harness.PushedAsync(socket, Replaced);

        await ShownAsync(0);
        await ServerPlaybackFixture.Until(() => LogLines("A listener of the server's activity failed") >= 1, "the failure in the log");
        Assert.True(Volatile.Read(ref later) >= 1);
    }

    [Fact]
    public async Task AChannelTurnedDownOnALaterConnection_TakesItsListAway()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions, EventsChannels.Scans);
        await ShownAsync(2);

        socket.Close();
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(1), "the wait before the next connection");
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        await _harness.OpenedAsync(1, EventsChannels.Scans);

        Assert.Null(_harness.State.Sessions);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AnAnswerForAChannelALaterConnectionNoLongerHas_IsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Library.Answer(SessionsPath, LibraryHarness.Held(release, AdminPayloads.Sessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions, EventsChannels.Scans);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read on its way");

        socket.Close();
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(1), "the wait before the next connection");
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        await _harness.OpenedAsync(1, EventsChannels.Scans);
        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(_harness.State.Sessions);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AnAnswerForTheLastProfile_IsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Library.Answer(SessionsPath, LibraryHarness.Held(release, AdminPayloads.Sessions));
        await _harness.StartedAsync(EventsChannels.Sessions);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read on its way");

        _harness.Library.Session.ProfileId = "8";
        _harness.Library.Session.RaiseChanged();
        // The new profile's connection is turned down: nothing of its own is read.
        await _harness.OpenedAsync(1);
        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(_harness.State.Sessions);
        Assert.False(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AnAnswerFromBeforeAnAccessChange_IsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Library.Answer(SessionsPath, LibraryHarness.Held(release, AdminPayloads.Sessions));
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read on its way");

        await _harness.PushedAsync(socket, """{ "type": "access_changed" }""");
        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(_harness.State.Sessions);
        Assert.False(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AfterAnAccessChange_NothingIsRead_UntilTheServerGivesTheChannelAgain()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow + TimeSpan.FromSeconds(1));

        await _harness.PushedAsync(socket, """{ "type": "access_changed" }""");
        await _harness.PushedAsync(socket, Replaced);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, Reads);
        Assert.Null(_harness.State.Sessions);

        socket.Close(4001);
        await _harness.OpenedAsync(1, EventsChannels.Sessions);
        await ShownAsync(2);
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task SigningOut_CancelsTheReadOnItsWay()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Library.Silo.OnCancellable(HttpMethod.Get, SessionsPath, async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("Never answered.");
        });
        await _harness.StartedAsync(EventsChannels.Sessions);
        await ServerPlaybackFixture.Until(() => Reads == 1, "the read on its way");

        _harness.Library.Session.State = Granary.Core.Auth.SessionState.SignedOut;
        _harness.Library.Session.RaiseChanged();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(ServerActivityState.None, _harness.State);
        Assert.DoesNotContain(_harness.Library.Logs.Lines, line => line.Contains("Could not read", StringComparison.Ordinal));
    }
}
