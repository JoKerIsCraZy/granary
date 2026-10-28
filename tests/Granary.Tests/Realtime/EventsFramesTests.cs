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
