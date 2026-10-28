using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The room operations: paths, bodies, and the room proof where the server asks for it.</summary>
public sealed class WatchPartyApiTests
{
    private const string Rooms = "api/v2/watch-together/rooms";
    private const string Room = Rooms + "/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();

    private WatchPartyApi Api => new(_silo.Http);

    private SentRequest Only() => Assert.Single(_silo.Sent);

    [Fact]
    public async Task Create_OpensAHostPickRoom_UnderTheIdItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Post, Rooms, ScriptedSilo.Json(HttpStatusCode.Created, WatchPartyPayloads.Answer(WatchPartyPayloads.Room())));

        var opened = await Api.CreateAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, ct);

        Assert.Equal("ABCD1234", opened.Room.Code);
        Assert.Equal(WatchPartyPayloads.Token, opened.RoomAccessToken);
        Assert.True(opened.Room.IsHost);
        Assert.Equal($$"""{"room_id":"{{WatchPartyPayloads.RoomId}}","selection_mode":"host_pick"}""", Only().Body);
        Assert.Equal(SiloAuth.Profile, Only().Auth);
    }

    [Fact]
    public async Task Join_SendsTheCodeOrTheInvitation()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: "guest"))));

        var joined = await Api.JoinAsync(ScriptedSilo.Base, new WatchPartyJoin { Code = "ABCD1234" }, ct);

        Assert.False(joined.Room.IsHost);
        Assert.Equal("Maya", joined.Room.Self!.DisplayName);
        Assert.Equal("""{"code":"ABCD1234"}""", Only().Body);
    }

    [Fact]
    public async Task ReadsAndTickets_CarryTheRoomProof_HostOperationsDoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        string answer = WatchPartyPayloads.Answer(WatchPartyPayloads.Room(selected: "movie:heat"));
        _silo.On(HttpMethod.Get, Room, ScriptedSilo.Json(HttpStatusCode.OK, answer));
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Put, Room + "/staged-selection", ScriptedSilo.Json(HttpStatusCode.OK, answer));
        _silo.On(HttpMethod.Post, Room + "/playback/start", ScriptedSilo.Json(HttpStatusCode.OK, answer));
        _silo.On(HttpMethod.Post, Room + "/playback/stop", ScriptedSilo.Json(HttpStatusCode.OK, answer));
        _silo.On(HttpMethod.Patch, Room + "/policy", ScriptedSilo.Json(HttpStatusCode.OK, answer));
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.NoContent());

        var read = await Api.GetAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, "proof-a", ct);
        var ticket = await Api.MintTicketAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, "proof-a", ct);
        await Api.StageAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, "movie:heat", ct);
        await Api.StartAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, ct);
        await Api.StopAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, ct);
        await Api.SetPolicyAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, guestsCanPause: true, ct);
        await Api.EndAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, ct);

        Assert.True(read.Room.IsStaged);
        Assert.True(ticket.IsValid);
        Assert.Equal(["proof-a", "proof-a", null, null, null, null, null], _silo.Sent.Select(r => r.RoomToken));
        Assert.Equal("""{"content_id":"movie:heat"}""", _silo.Sent[2].Body);
        Assert.Equal("""{"guest_control_policy":"guest_play_pause"}""", _silo.Sent[5].Body);
    }

    [Fact]
    public void TheSocket_AndTheInvitation_LiveUnderTheServer()
    {
        var room = JsonSerializer.Deserialize(WatchPartyPayloads.Room(), WatchPartyJsonContext.Default.WatchPartyRoom)!;

        Assert.Equal(new Uri($"wss://nas.example/silo/api/v2/watch-together/rooms/{WatchPartyPayloads.RoomId}/ws"),
            WatchPartyApi.SocketUri(ScriptedSilo.Base, WatchPartyPayloads.RoomId));
        Assert.Equal(new Uri("https://nas.example/silo/rooms?token=invite-secret"), WatchPartyApi.InviteLink(ScriptedSilo.Base, room));
        Assert.Null(WatchPartyApi.InviteLink(ScriptedSilo.Base, room with { InvitePath = "//evil.example/x" }));
    }

    [Fact]
    public void ATicket_IsOnlyOneOfThisProtocolAndShape()
    {
        Assert.True(new WatchPartySocketTicket(WatchPartyPayloads.Ticket, 30, 300, "silo.room.v2").IsValid);
        Assert.False(new WatchPartySocketTicket(WatchPartyPayloads.Ticket, 30, 300, "silo.events.v2").IsValid);
        Assert.False(new WatchPartySocketTicket("short", 30, 300, "silo.room.v2").IsValid);
        Assert.False(new WatchPartySocketTicket(WatchPartyPayloads.Ticket[..42] + "\r", 30, 300, "silo.room.v2").IsValid);
    }

    [Theory]
    [InlineData("abcd1234", "ABCD1234", null)]
    [InlineData(" ABCD-1234 ", "ABCD1234", null)]
    [InlineData("https://silo.example/rooms?token=abc%2B1", null, "abc+1")]
    [InlineData("/rooms?x=1&token=tok-2#frag", null, "tok-2")]
    public void AJoin_TakesACodeOrAnInvitationLink(string input, string? code, string? token)
    {
        var join = WatchPartySession.ParseJoin(input);

        Assert.Equal((code, token), (join?.Code, join?.JoinToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("hello there!")]
    public void Neither_IsNoJoin(string input) => Assert.Null(WatchPartySession.ParseJoin(input));
}
