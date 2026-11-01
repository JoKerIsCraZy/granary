using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>
/// A room that is over, as Silo's apps take it: the room page gives way to the hub, which says why in the web's words
/// (<c>WatchTogetherRoomPage.tsx</c>'s <c>RoomTerminalState</c>) and lists the room as ended among the recent parties.
/// </summary>
public sealed class WatchPartyEndTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;
    private const string StartNew = "Start a new watch party or join another room to keep watching together.";

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly WatchPartySession _party;
    private readonly WatchPartyApi _api;

    public WatchPartyEndTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Get, "api/v2/watch-together/capabilities", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "lobby_ready": true, "stop_playback": true }"""));
        _api = new WatchPartyApi(_silo.Http);
        var recent = new WatchPartyRecentRooms(_secrets, _time, _logs.For<WatchPartyRecentRooms>());
        _party = new WatchPartySession(_session, _api, () => new WatchPartyConnection(_api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>(), recent, _time);
    }

    private FakeRealtimeSocket Socket => _sockets[^1];

    private WatchPartyHubViewModel Hub() => new(_party, _navigation, _logs.For<WatchPartyHubViewModel>());

    private WatchPartyRoomViewModel RoomPage() => new(_party, new WatchPartyFeature(_session, _api, _time, _logs.For<WatchPartyFeature>()), _session,
        new CatalogApi(_silo.Http), new ImageParameterCache(new ImagesApi(_silo.Http), _logs.For<ImageParameterCache>()), _navigation,
        new ImmediateDispatcher(), _time, _logs.For<WatchPartyRoomViewModel>());

    private async Task<WatchPartyRoomViewModel> InTheRoomAsync(string role = "guest")
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: role))));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        var page = RoomPage();
        page.Activate();
        return page;
    }

    private Task Marked() => ServerPlaybackFixture.Until(
        () => _secrets.Values.TryGetValue(WatchPartyRecentRooms.SecretKey, out string? rooms) && rooms.Contains("\"ended\":true", StringComparison.Ordinal),
        "the recent room marked as ended");

    [Theory]
    [InlineData("host_left", "The room has ended.")]
    [InlineData("room_closed", "The room has ended.")]
    [InlineData("ended", "That watch party is no longer active.")]
    [InlineData("not_found", "Room not found.")]
    [InlineData("forbidden", "You don't have access to this watch party.")]
    [InlineData("something_new", "Room is unavailable.")]
    public void AClosedRoom_IsSaidInTheWebsWords(string reason, string title)
    {
        var end = new WatchPartyEnd(reason);

        Assert.Equal((title, StartNew), (WatchPartyText.EndedTitle(end), WatchPartyText.EndedText(end)));
    }

    [Fact]
    public void AReplacement_IsSaidWithTheServersReason_OrTheWebsOwn()
    {
        var told = new WatchPartyEnd("replaced", Replaced: true, "Joined from the living room TV.");
        var untold = new WatchPartyEnd("replaced", Replaced: true);

        Assert.Equal(("Watch Party joined on another device", "Joined from the living room TV."), (WatchPartyText.EndedTitle(told), WatchPartyText.EndedText(told)));
        Assert.Equal(WatchPartyFrames.ReplacedText, WatchPartyText.EndedText(untold));
    }

    [Fact]
    public async Task TheRoomPage_HidesTheRoom_AndGivesWayToTheHub_WhenTheRoomCloses()
    {
        var page = await InTheRoomAsync();
        Assert.True(page.ShowsRoom);

        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _navigation.Sections.Count == 1, "the way to the hub");

        Assert.False(page.ShowsRoom);
        Assert.Equal(AppRoute.WatchParty, _navigation.Sections[0]);
        Assert.DoesNotContain(_navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
        page.Deactivate();
    }

    [Fact]
    public async Task TheHub_SaysTheRoomEnded_AndListsItAsEnded_InsteadOfTheRoomItWas()
    {
        var page = await InTheRoomAsync();
        var hub = Hub();
        Assert.True(hub.IsInRoom);

        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => hub.HasEnded, "the end on the hub");
        await Marked();
        await hub.RefreshRecentAsync();

        Assert.False(hub.IsInRoom);
        Assert.Equal(("The room has ended.", StartNew, false), (hub.EndedTitle, hub.EndedText, hub.CanRejoin));
        var row = Assert.Single(hub.Recent);
        Assert.Equal(("Ended", "ENDED", false), (row.Status, row.EndLabel, row.CanRejoin));
        // Ended in the list already: nobody asked the server again.
        Assert.Empty(_silo.SentTo(HttpMethod.Get, Room));
        page.Deactivate();
    }

    [Fact]
    public async Task TheEndSaidOnTheHub_GoesWhenDismissed()
    {
        var page = await InTheRoomAsync();
        var hub = Hub();
        Socket.Push("""{ "type": "room_closed", "reason": "ended" }""");
        await ServerPlaybackFixture.Until(() => hub.HasEnded, "the end on the hub");
        Assert.Equal("That watch party is no longer active.", hub.EndedTitle);

        await hub.DismissEndCommand.ExecuteAsync(null);

        Assert.False(hub.HasEnded);
        Assert.Null(_party.Room);
        page.Deactivate();
    }

    [Fact]
    public async Task TheHostsEnd_GoesToTheHub_WhichSaysTheRoomEnded()
    {
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.NoContent());
        var page = await InTheRoomAsync(role: "host");
        var hub = Hub();

        await page.EndRoomAsync();

        Assert.Single(_silo.SentTo(HttpMethod.Delete, Room));
        Assert.Equal(AppRoute.WatchParty, _navigation.Navigations[^1].Route);
        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
        Assert.True(Socket.Disposed);
        Assert.Equal(("The room has ended.", StartNew), (hub.EndedTitle, hub.EndedText));
        Assert.False(hub.IsInRoom);
        await Marked();
        page.Deactivate();
    }

    [Fact]
    public async Task TheRoomsOwnClose_BeforeTheHostsEndIsAnswered_SaysTheSame()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Delete, Room, async () =>
        {
            await answered.Task;
            return await ScriptedSilo.NoContent()();
        });
        var page = await InTheRoomAsync(role: "host");
        var hub = Hub();

        var ending = page.EndRoomAsync();
        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => hub.HasEnded, "the room's own close");
        answered.SetResult();
        await ending;

        Assert.Equal("The room has ended.", hub.EndedTitle);
        Assert.All(_navigation.Navigations, n => Assert.Equal(AppRoute.WatchParty, n.Route));
        page.Deactivate();
    }

    [Fact]
    public async Task AnEndRefusedAfterTheRoomsOwnClose_EndsAllTheSame()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Delete, Room, async () =>
        {
            await answered.Task;
            return await ScriptedSilo.Problem(409, "room_closed")();
        });
        var page = await InTheRoomAsync(role: "host");
        var hub = Hub();

        var ending = page.EndRoomAsync();
        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => hub.HasEnded, "the room's own close");
        answered.SetResult();
        await ending;

        Assert.Equal("", page.Message);
        Assert.Equal("The room has ended.", hub.EndedTitle);
        Assert.True(Socket.Disposed);
        page.Deactivate();
    }

    [Fact]
    public async Task AnEndRefusedBeforeTheRoomClosed_IsAFailure()
    {
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.Problem(409, "conflict"));
        var page = await InTheRoomAsync(role: "host");

        await page.EndRoomAsync();

        Assert.StartsWith("Could not end the room.", page.Message, StringComparison.Ordinal);
        Assert.Null(_party.End);
        Assert.True(_party.IsInRoom);
        page.Deactivate();
    }

    [Fact]
    public async Task ADoubleEnd_SendsOneEnd()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Delete, Room, async () =>
        {
            await answered.Task;
            return await ScriptedSilo.NoContent()();
        });
        var page = await InTheRoomAsync(role: "host");

        var first = _party.EndRoomAsync();
        var second = _party.EndRoomAsync();
        answered.SetResult();

        Assert.Equal((true, true), (await first, await second));
        Assert.False(await _party.EndRoomAsync());
        Assert.Single(_silo.SentTo(HttpMethod.Delete, Room));
        Assert.Equal("host_left", _party.End?.Reason);
        page.Deactivate();
    }

    [Fact]
    public async Task AnEndAnsweredAfterTheRoomWasLeft_LeavesNothingBehind_ButMarksTheRoom()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Delete, Room, async () =>
        {
            await answered.Task;
            return await ScriptedSilo.NoContent()();
        });
        var page = await InTheRoomAsync(role: "host");
        var hub = Hub();

        var ending = _party.EndRoomAsync();
        await _party.LeaveAsync();
        answered.SetResult();

        Assert.True(await ending);
        Assert.Null(_party.End);
        Assert.False(hub.HasEnded);
        await Marked();
        page.Deactivate();
    }

    [Fact]
    public async Task AnEndAnsweredAfterTheRoomWasEnteredAgain_LeavesTheNewConnectionAlone()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Delete, Room, async () =>
        {
            await answered.Task;
            return await ScriptedSilo.NoContent()();
        });
        var page = await InTheRoomAsync(role: "host");

        var ending = _party.EndRoomAsync();
        await _party.LeaveAsync();
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && _party.IsConnected, "the room entered again");
        answered.SetResult();
        await ending;

        Assert.False(Socket.Disposed);
        Assert.True(_party.IsInRoom);
        Assert.Null(_party.End);
        page.Deactivate();
    }

    [Fact]
    public async Task TheRoomPageUnderThePlayer_AndThePlayer_GoToTheHubOnce()
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(
            WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat"))));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        var page = RoomPage();
        page.Activate();
        Assert.Equal(AppRoute.Player, _navigation.Current);
        var player = new PlayerViewModel(new FakePlaybackEngine(), new ImmediateDispatcher(), _navigation, () => new FakeServerPlayback(),
            new PlaybackStops(), _time, watchParty: _party);
        await player.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));

        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _navigation.PlayerLeaves == 1, "the player leaving");

        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
        page.Deactivate();
    }

    [Fact]
    public async Task ATakeover_GoesToTheHub_WhichOffersTheRoomBack()
    {
        var page = await InTheRoomAsync();
        var hub = Hub();

        Socket.Push("""{ "type": "connection_replaced", "reason": "This profile joined the Watch Party on another device." }""");
        await ServerPlaybackFixture.Until(() => _navigation.Sections.Count == 1, "the way to the hub");
        page.Deactivate();

        Assert.Equal(("Watch Party joined on another device", "This profile joined the Watch Party on another device.", true),
            (hub.EndedTitle, hub.EndedText, hub.CanRejoin));

        hub.RejoinCommand.Execute(null);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && _party.IsConnected, "the room claimed back");

        Assert.False(hub.HasEnded);
        Assert.Equal(AppRoute.WatchPartyRoom, _navigation.Navigations[^1].Route);
    }

    [Fact]
    public async Task ARoomPageOpenedOnARoomThatEnded_GoesStraightToTheHub()
    {
        var page = await InTheRoomAsync();
        page.Deactivate();
        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _party.End is not null, "the end");
        Assert.Empty(_navigation.Sections);

        var again = RoomPage();
        again.Activate();

        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
        again.Deactivate();
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
