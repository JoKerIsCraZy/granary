using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Events;
using Granary.Core.Realtime;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>The events stream's frames, its ticket and its address (docs/api-contract.md, 12).</summary>
public sealed class EventsFramesTests
{
    [Fact]
    public void Subscribe_NamesTheChannels()
    {
        var frame = JsonDocument.Parse(EventsFrames.Subscribe(["catalog", "user_state", "notifications"], "req-1")).RootElement;

        Assert.Equal("subscribe", frame.GetProperty("type").GetString());
        Assert.Equal("req-1", frame.GetProperty("request_id").GetString());
        Assert.Equal(["catalog", "user_state", "notifications"], frame.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void AnEvent_IsReadWithItsChannelNameAndData()
    {
        var frame = EventsFrames.Parse("""
            { "type": "event", "channel": "user_state", "event": "user_state.changed", "event_id": "e1", "timestamp": "2026-10-01T12:00:00Z",
              "data": { "profile_id": "7", "content_id": "movie:heat", "change": "watched" } }
            """);

        Assert.NotNull(frame);
        Assert.Equal((EventsFrameTypes.Event, "user_state", "user_state.changed"), (frame.Type, frame.Channel, frame.Event));
        Assert.Equal("7", frame.Text("profile_id"));
        Assert.Equal("watched", frame.Text("change"));
        Assert.Null(frame.Text("series_id"));
    }

    [Fact]
    public void Subscribed_SaysWhatWasRejected()
    {
        var frame = EventsFrames.Parse("""
            { "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "user_state"],
              "rejected": [ { "channel": "notifications", "code": "profile_required" } ] }
            """);

        Assert.NotNull(frame);
        Assert.Equal(EventsFrameTypes.Subscribed, frame.Type);
        Assert.Equal(["notifications"], frame.Rejected);
        Assert.Equal(["catalog", "user_state"], frame.Accepted);
    }

    [Fact]
    public void AFrameCutShort_IsReadByWhatItsHeadNames()
    {
        // What the socket keeps of a frame larger than the lane takes: its first bytes.
        var snapshot = EventsFrames.Parse("""{"type":"snapshot","channel":"sessions","timestamp":"2026-10-01T12:00:00Z","data":[{"session_id":"s1","username":"ele""");
        var replaced = EventsFrames.Parse("""{"type":"event","channel":"sessions","event":"sessions.replaced","event_id":"e1","timestamp":"t","data":[{"sess""");

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsCut);
        Assert.Equal((EventsFrameTypes.Snapshot, "sessions"), (snapshot.Type, snapshot.Channel));
        Assert.Equal(JsonValueKind.Undefined, snapshot.Data.ValueKind);
        Assert.NotNull(replaced);
        Assert.Equal((EventsFrameTypes.Event, "sessions", "sessions.replaced", true), (replaced.Type, replaced.Channel, replaced.Event, replaced.IsCut));
    }

    [Fact]
    public void AWholeFrame_IsNotCut() =>
        Assert.False(EventsFrames.Parse("""{ "type": "snapshot", "channel": "sessions", "timestamp": "t", "data": [] }""")!.IsCut);

    // The lists these frames tell of are read from the server: their own rows are never looked at.
    [Theory]
    [InlineData("""{"type":"event","channel":"sessions","event":"sessions.replaced","event_id":"e1","timestamp":"t","data":[{"session_id":"s1"}]}""")]
    [InlineData("""{"type":"snapshot","channel":"sessions","timestamp":"t","data":[{"session_id":"s1"}]}""")]
    [InlineData("""{"type":"event","channel":"tasks","event":"task.updated","event_id":"e1","timestamp":"t","data":{"key":"backup"}}""")]
    [InlineData("""{"type":"snapshot","channel":"tasks","timestamp":"t","data":[{"key":"backup"}]}""")]
    [InlineData("""{"type":"event","channel":"download_preparations","event":"download_preparation.changed","event_id":"e1","timestamp":"t","data":{"id":"job-1"}}""")]
    [InlineData("""{"type":"event","channel":"download_preparations","event":"download_storage.changed","event_id":"e1","timestamp":"t","data":{"a":1}}""")]
    public void AFrameThatIsOnlyACue_KeepsNoPayload_AndNeedNotBeTakenWhole(string text)
    {
        var frame = EventsFrames.Parse(text);

        Assert.NotNull(frame);
        Assert.Equal(JsonValueKind.Undefined, frame.Data.ValueKind);
        Assert.False(frame.IsCut);
        Assert.True(EventsFrames.IsCue(frame.Type, frame.Channel, frame.Event));
        Assert.False(EventsFrames.TakesWhole(System.Text.Encoding.UTF8.GetBytes(text[..^8])));
    }

    [Theory]
    [InlineData("""{"type":"event","channel":"user_state","event":"user_state.changed","event_id":"e1","timestamp":"t","data":{"profile_id":"7","change":"watched"}}""", JsonValueKind.Object)]
    [InlineData("""{"type":"event","channel":"catalog","event":"catalog.item.changed","event_id":"e1","timestamp":"t","data":{"content_id":"movie:heat"}}""", JsonValueKind.Object)]
    [InlineData("""{"type":"snapshot","channel":"notifications","timestamp":"t","data":[{"id":"n1"}]}""", JsonValueKind.Array)]
    [InlineData("""{"type":"event","channel":"notifications","event":"notification.created","event_id":"e1","timestamp":"t","data":{"id":"n1"}}""", JsonValueKind.Object)]
    [InlineData("""{"type":"snapshot","channel":"scans","timestamp":"t","data":[{"id":"a"}]}""", JsonValueKind.Array)]
    [InlineData("""{"type":"event","channel":"scans","event":"scan.progress","event_id":"e1","timestamp":"t","data":{"id":"a"}}""", JsonValueKind.Object)]
    [InlineData("""{"type":"event","channel":"download_preparations","event":"download_preparation.progress","event_id":"e1","timestamp":"t","data":{"id":"job-1"}}""", JsonValueKind.Object)]
    [InlineData("""{"type":"event","channel":"something_new","event":"new.thing","event_id":"e1","timestamp":"t","data":{"id":"x"}}""", JsonValueKind.Object)]
    public void AFrameWhosePayloadIsUsed_KeepsIt_AndIsTakenWhole(string text, JsonValueKind payload)
    {
        var frame = EventsFrames.Parse(text);

        Assert.NotNull(frame);
        Assert.Equal(payload, frame.Data.ValueKind);
        Assert.True(EventsFrames.TakesWhole(System.Text.Encoding.UTF8.GetBytes(text[..^8])));
    }

    [Theory]
    [InlineData("""{"data":[{"type":"event","channel":"sessions","eve""")]
    [InlineData("""{"type":"error","code":"internal_error","message":"Failed to lo""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AHeadThatDoesNotNameACue_IsTakenWhole(string head) =>
        Assert.True(EventsFrames.TakesWhole(System.Text.Encoding.UTF8.GetBytes(head)));

    [Fact]
    public void Hello_SaysWhichChannelsTheServerGives()
    {
        var hello = EventsFrames.Parse("""{ "type": "hello", "schema_version": 1, "connection_id": "c", "available_channels": ["catalog", "sessions"], "required_action": "subscribe" }""");
        var older = EventsFrames.Parse("""{ "type": "hello", "schema_version": 1, "connection_id": "c" }""");
        var none = EventsFrames.Parse("""{ "type": "hello", "schema_version": 1, "connection_id": "c", "available_channels": null }""");

        Assert.Equal(["catalog", "sessions"], hello!.Available);
        Assert.Null(older!.Available);
        Assert.Null(none!.Available);
    }

    [Theory]
    [InlineData("""{ "type": "hello", "schema_version": 2, "connection_id": "c", "available_channels": ["catalog"], "required_action": "subscribe" }""", "hello")]
    [InlineData("""{ "type": "snapshot", "channel": "notifications", "timestamp": "t", "data": [] }""", "snapshot")]
    [InlineData("""{ "type": "access_changed" }""", "access_changed")]
    [InlineData("""{ "type": "error", "code": "subscribe_timeout", "message": "m" }""", "error")]
    [InlineData("""{ "type": "something_new" }""", "something_new")]
    public void EveryFrame_IsReadByItsType(string text, string type) => Assert.Equal(type, EventsFrames.Parse(text)?.Type);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "channel": "catalog" }""")]
    [InlineData("""{ "type": 3 }""")]
    [InlineData("""{ "channel": "sessions", "data": [{ "type": "eve""")]
    [InlineData("""{ "type": "event" "channel" """)]
    public void WhatIsNoFrame_IsNothing(string text) => Assert.Null(EventsFrames.Parse(text));

    [Fact]
    public void TheSocket_IsOnTheServersAddress_WithoutCredentials()
    {
        Assert.Equal(new Uri("wss://nas.example/silo/api/v2/events/ws"), EventsApi.SocketUri(new Uri("https://user:pw@nas.example/silo/?x=1#f")));
        Assert.Equal(new Uri("ws://192.168.1.10:8090/api/v2/events/ws"), EventsApi.SocketUri(new Uri("http://192.168.1.10:8090")));
    }

    [Fact]
    public async Task TheTicket_IsAskedForTheProfile()
    {
        var silo = new ScriptedSilo();
        silo.On(HttpMethod.Post, "api/v2/events/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "ticket": "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde", "expires_in": 30, "max_connection_seconds": 300, "protocol": "silo.events.v2" }"""));

        var ticket = await new EventsApi(silo.Http).MintTicketAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);

        var sent = Assert.Single(silo.SentTo(HttpMethod.Post, "api/v2/events/ws-ticket"));
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.True(ticket.IsValid);
        Assert.Equal(300, ticket.MaxConnectionSeconds);
    }

    [Theory]
    [InlineData("silo.events.v1", 30, 300, "abc")]
    [InlineData("silo.events.v2", 31, 300, "abc")]
    [InlineData("silo.events.v2", 30, 0, "abc")]
    [InlineData("silo.events.v2", 30, 300, "")]
    [InlineData("silo.events.v2", 30, 300, "a b")]
    [InlineData("silo.events.v2", 30, 300, "a,b")]
    public void ATicketOutsideTheContract_IsNotOne(string protocol, int expiresIn, int maxSeconds, string value) =>
        Assert.False(new EventsTicket(value, expiresIn, maxSeconds, protocol).IsValid);
}
