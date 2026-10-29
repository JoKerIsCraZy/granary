using System.Net;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The room's suggestions, its activity and the recent rooms, as the session keeps them.</summary>
public sealed class WatchPartySessionSuggestionsTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly InMemorySecretStore _secrets = new();
    private readonly ManualTimeProvider _time = new();
    private readonly WatchPartyRecentRooms _recent;
    private readonly WatchPartySession _party;

    public WatchPartySessionSuggestionsTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Get, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Suggestions(WatchPartyPayloads.Suggestion("s1", "Heat", votes: 1, mine: true))));
        var api = new WatchPartyApi(_silo.Http);
        _recent = new WatchPartyRecentRooms(_secrets, _time, _logs.For<WatchPartyRecentRooms>());
        _party = new WatchPartySession(_session, api, () => new WatchPartyConnection(api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>(), _recent, _time);
    }

    private FakeRealtimeSocket Socket => _sockets[^1];

    private async Task JoinAsync(string room)
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected && _party.Suggestions.Count > 0, "the room and its suggestions");
    }

    [Fact]
    public async Task EachConnection_ReadsTheSuggestions_AndABroadcastKeepsThisProfilesVotes()
    {
        await JoinAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        Assert.True(Assert.Single(_party.Suggestions).VotedByMe);

        Socket.Push(WatchPartyPayloads.SuggestionsUpdate(("s2", "Alien", 0), ("s1", "Heat", 3)));
        await ServerPlaybackFixture.Until(() => _party.Suggestions.Count == 2, "the broadcast");

        Assert.Equal([("Heat", true), ("Alien", false)], _party.Suggestions.Select(s => (s.Title, s.VotedByMe)));
    }

    [Fact]
    public async Task Suggesting_Voting_AndRemoving_ReadTheListAgain()
    {
        await JoinAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        _silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.Created, """{ "suggestion_id": "s2" }"""));
        _silo.On(HttpMethod.Post, Room + "/suggestions/s2/vote", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Get, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Suggestions(
            WatchPartyPayloads.Suggestion("s2", "Alien", votes: 1, mine: true, user: "2", profile: "9"),
            WatchPartyPayloads.Suggestion("s1", "Heat", votes: 1, mine: true, created: "2026-10-01T12:30:00Z"))));

        await _party.SuggestAsync(new WatchPartySuggestionCreate(Guid.NewGuid().ToString(), "movie:alien", "movie", "Alien"));
        await _party.SetVoteAsync("s2", vote: true);

        Assert.Equal(["Alien", "Heat"], _party.Suggestions.Select(s => s.Title));
        Assert.Equal(3, _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count);
    }

    [Fact]
    public async Task TheHost_SwitchesToVoting_AndPlaysASuggestion_TheAnswersShow()
    {
        await JoinAsync(WatchPartyPayloads.Room());
        _silo.On(HttpMethod.Patch, Room + "/selection-mode",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 2, mode: "vote"))));
        _silo.On(HttpMethod.Post, Room + "/suggestions/promote", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room("playing", "waiting", generation: 3, revision: 1, selected: "movie:heat", mode: "vote"))));

        await _party.SetSelectionModeAsync(WatchPartySelectionModes.Vote);
        Assert.Equal("vote", _party.Room!.SelectionMode);
        await _party.PromoteAsync("s1");

        Assert.True(_party.Room!.IsPlaying);
        Assert.Equal(WatchPartyPayloads.Token, _silo.SentTo(HttpMethod.Post, Room + "/suggestions/promote")[0].RoomToken);
    }

    [Fact]
    public async Task TheActivity_StartsAfterTheFirstConnectedSnapshot()
    {
        await JoinAsync(WatchPartyPayloads.Room());
        Assert.Empty(_party.Activity);

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 2, mode: "vote")));
        // The entry, not only the room: the session sets the room before it notes what changed, and this polls from
        // another thread.
        await ServerPlaybackFixture.Until(() => _party.Room!.Generation == 2 && _party.Activity.Count > 0, "the snapshot and its entry");

        var entry = Assert.Single(_party.Activity);
        Assert.Equal((WatchPartyActivityKinds.Mode, "Elena", "vote"), (entry.Kind, entry.Who, entry.Detail));
    }

    [Fact]
    public async Task AnEnteredRoom_IsRemembered_AndOneThatEnded_IsMarked()
    {
        await JoinAsync(WatchPartyPayloads.Room(role: "guest"));
        await ServerPlaybackFixture.Until(() => _secrets.Values.ContainsKey(WatchPartyRecentRooms.SecretKey), "the recent room");
        var recent = Assert.Single(await _party.ListRecentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("ABCD1234", WatchPartyPayloads.Token, false, false), (recent.Code, recent.Token, recent.IsHost, recent.Ended));

        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _party.End is not null, "the end");
        await ServerPlaybackFixture.Until(() => _secrets.Values[WatchPartyRecentRooms.SecretKey].Contains("\"ended\":true", StringComparison.Ordinal), "the mark");
    }

    [Fact]
    public async Task ARecentRoom_IsAskedWhetherItIsOn_AndGoneIfItsProofIsRefused()
    {
        var room = new WatchPartyRecentRoom(ScriptedSilo.Server.Id, "7", WatchPartyPayloads.RoomId, "ABCD1234", WatchPartyPayloads.Token, true, _time.GetUtcNow());
        await _recent.RememberAsync(room, TestContext.Current.CancellationToken);
        _silo.On(HttpMethod.Get, Room, ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room())),
            ScriptedSilo.Problem(409, "conflict"), ScriptedSilo.Problem(403, "forbidden"));

        Assert.Equal(WatchPartyRecentState.Live, await _party.CheckRecentAsync(room, TestContext.Current.CancellationToken));
        Assert.Equal(WatchPartyRecentState.Ended, await _party.CheckRecentAsync(room, TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(await _party.ListRecentAsync(TestContext.Current.CancellationToken)).Ended);
        Assert.Equal(WatchPartyRecentState.Gone, await _party.CheckRecentAsync(room, TestContext.Current.CancellationToken));
        Assert.Empty(await _party.ListRecentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecentRooms_AreOfTheLastDay_EightAtMost_PerProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = _time.GetUtcNow();
        for (int i = 0; i < 10; i++)
        {
            await _recent.RememberAsync(new WatchPartyRecentRoom("srv1", "7", $"room-{i}", $"CODE{i}", "proof", false, start.AddMinutes(i)), ct);
        }
        await _recent.RememberAsync(new WatchPartyRecentRoom("srv1", "8", "other", "OTHER", "proof", false, start), ct);

        var mine = await _recent.ListAsync("srv1", "7", ct);
        Assert.Equal(8, mine.Count);
        Assert.Equal("room-9", mine[0].RoomId);

        _time.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(8.5)));
        Assert.Equal(["room-9"], (await _recent.ListAsync("srv1", "7", ct)).Select(r => r.RoomId));
    }

    [Fact]
    public async Task ARefusedProof_LeavesTheRecentRoom_ForTheHubToAskAgain()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Problem(403, "forbidden"));
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: "guest"))));

        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _party.End is not null, "the refused ticket");
        await ServerPlaybackFixture.Until(() => _secrets.Values.ContainsKey(WatchPartyRecentRooms.SecretKey), "the recent room");

        Assert.Equal("forbidden", _party.End!.Reason);
        var recent = Assert.Single(await _party.ListRecentAsync(TestContext.Current.CancellationToken));
        Assert.Equal((false, WatchPartyPayloads.Token), (recent.Ended, recent.Token));
    }

    [Fact]
    public async Task AnOlderListOverHttp_DoesNotUndoANewerBroadcast_OnlyThisProfilesVotesCount()
    {
        await JoinAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, Room + "/suggestions/s2/vote", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Get, Room + "/suggestions", async () =>
        {
            await answered.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Suggestions(
                WatchPartyPayloads.Suggestion("s1", "Heat", votes: 1, mine: true),
                WatchPartyPayloads.Suggestion("s2", "Alien", votes: 1, mine: true)))();
        });
        int reads = _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count;

        var voting = _party.SetVoteAsync("s2", vote: true);
        await ServerPlaybackFixture.Until(() => _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count > reads, "the read back");
        Socket.Push(WatchPartyPayloads.SuggestionsUpdate(("s2", "Alien", 4), ("s1", "Heat", 2), ("s3", "Ronin", 0)));
        await ServerPlaybackFixture.Until(() => _party.Suggestions.Count == 3, "the broadcast");
        answered.SetResult();
        await voting;

        Assert.Equal([("Alien", 4, true), ("Heat", 2, true), ("Ronin", 0, false)], _party.Suggestions.Select(s => (s.Title, s.VoteCount, s.VotedByMe)));
    }

    [Fact]
    public async Task AClearRefusal_IsNotReadBack_AnUncertainFailureIs()
    {
        await JoinAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        _silo.On(HttpMethod.Post, Room + "/suggestions/s1/vote", ScriptedSilo.Problem(409, "conflict"), ScriptedSilo.NetworkError());
        int reads = _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count;

        await Assert.ThrowsAsync<Granary.Core.Api.SiloApiException>(() => _party.SetVoteAsync("s1", vote: true));
        Assert.Equal(reads, _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count);
        await Assert.ThrowsAsync<HttpRequestException>(() => _party.SetVoteAsync("s1", vote: true));

        Assert.Equal(reads + 1, _silo.SentTo(HttpMethod.Get, Room + "/suggestions").Count);
    }

    [Fact]
    public async Task TheHub_OffersTheRecentRooms_AndGoesBackIntoALiveOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow();
        await _recent.RememberAsync(new WatchPartyRecentRoom("srv1", "7", WatchPartyPayloads.RoomId, "ABCD1234", WatchPartyPayloads.Token, true, now,
            "Heat (1995)"), ct);
        await _recent.RememberAsync(new WatchPartyRecentRoom("srv1", "7", "old-room", "OLD12345", "proof-2", false, now.AddMinutes(-5)), ct);
        _silo.On(HttpMethod.Get, Room, ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room())));
        _silo.On(HttpMethod.Get, "api/v2/watch-together/rooms/old-room", ScriptedSilo.Problem(409, "conflict"));
        var navigation = new RecordingNavigation();
        var hub = new Granary.Core.ViewModels.WatchParty.WatchPartyHubViewModel(_party, navigation, _logs.For<Granary.Core.ViewModels.WatchParty.WatchPartyHubViewModel>());

        await hub.RefreshRecentAsync();

        Assert.Equal([("Heat (1995)", "Live", "Your room", true), ("Watch Party", "Ended", "Joined", false)],
            hub.Recent.Select(r => (r.Title, r.Status, r.Role, r.CanRejoin)));
        await hub.RejoinRecentCommand.ExecuteAsync(hub.Recent[0]);

        Assert.Equal(Granary.Core.Abstractions.AppRoute.WatchPartyRoom, navigation.Navigations[^1].Route);
        Assert.True(_party.IsInRoom);
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
