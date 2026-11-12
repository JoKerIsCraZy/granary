using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.Events;
using Granary.Core.Realtime;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The events lane over a real WebSocket: a local server takes the upgrade as Silo's does (the protocol chosen, the ticket
/// offered beside it), says hello, takes the subscription and sends an event; an access change closes with 4001.
/// </summary>
public sealed class EventsSocketTests : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Uri _server;
    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    public EventsSocketTests()
    {
        (_listener, _server) = LoopbackHttpListener.Start();
        _silo.On(HttpMethod.Post, new Uri(_server, EventsLaneTests.TicketPath), ScriptedSilo.Json(HttpStatusCode.OK, EventsLaneTests.Ticket));
    }

    [Fact]
    public async Task Lane_TalksTheProtocol_OverARealSocket()
    {
        var token = TestContext.Current.CancellationToken;
        var frames = new List<EventsFrame>();
        var gotEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotAccessChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lane = new EventsLane(new EventsApi(_silo.Http), () => new WebSocketRealtimeSocket(), TimeProvider.System, _logs.For<EventsLane>());

        lane.Follow(_server, ["catalog", "user_state"], frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }
            if (frame.Type == EventsFrameTypes.Event)
            {
                gotEvent.TrySetResult();
            }
            if (frame.Type == EventsFrameTypes.AccessChanged)
            {
                gotAccessChange.TrySetResult();
            }
        });
        var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(context.Request.IsWebSocketRequest);
        Assert.Equal("/api/v2/events/ws", context.Request.Url!.AbsolutePath);
        Assert.Equal("silo.events.v2, silo.ticket." + EventsLaneTests.TicketValue, context.Request.Headers["Sec-WebSocket-Protocol"]);
        Assert.Null(context.Request.Headers["Authorization"]);
        var accepted = await context.AcceptWebSocketAsync("silo.events.v2");
        using var socket = accepted.WebSocket;

        await SendAsync(socket, EventsLaneTests.Hello, token);
        var subscribe = await ReceiveAsync(socket, token);
        Assert.Equal("subscribe", subscribe.GetProperty("type").GetString());
        Assert.Equal(["catalog", "user_state"], subscribe.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        await SendAsync(socket, """{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "user_state"] }""", token);
        await SendAsync(socket, """{ "type": "event", "channel": "user_state", "event": "user_state.changed", "event_id": "e1", "timestamp": "t", "data": { "profile_id": "7", "change": "favorite" } }""", token);
        await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // The server's access check closes with 4001, without the frame before it.
        await socket.CloseOutputAsync((WebSocketCloseStatus)4001, "access_changed", token);
        await gotAccessChange.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        lock (frames)
        {
            Assert.Equal([EventsFrameTypes.Subscribed, EventsFrameTypes.Event, EventsFrameTypes.AccessChanged], frames.Select(f => f.Type));
            Assert.Equal("favorite", frames[1].Text("change"));
        }
        // Back at once, with a ticket of its own.
        var again = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(again.Request.IsWebSocketRequest);
        Assert.Equal(2, _silo.Sent.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/" + EventsLaneTests.TicketPath));
        again.Response.Abort();
        await lane.StopAsync();
    }

    [Fact]
    public async Task AFrameLargerThanTheLaneTakes_IsCutToItsHead_AndTheConnectionStays()
    {
        var token = TestContext.Current.CancellationToken;
        var frames = new List<EventsFrame>();
        var gotEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The app's sockets: frames up to the lane's limit whole, of larger ones the head.
        await using var lane = new EventsLane(new EventsApi(_silo.Http),
            () => new WebSocketRealtimeSocket(EventsLane.MaxFrameBytes, EventsLane.OversizeHeadBytes), TimeProvider.System, _logs.For<EventsLane>());
        lane.Follow(_server, ["catalog", "sessions"], frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }
            if (frame.Type == EventsFrameTypes.Event)
            {
                gotEvent.TrySetResult();
            }
        });
        var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
        var accepted = await context.AcceptWebSocketAsync("silo.events.v2");
        using var socket = accepted.WebSocket;
        await SendAsync(socket, EventsLaneTests.Hello, token);
        await ReceiveAsync(socket, token);
        await SendAsync(socket, """{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "sessions"] }""", token);

        // Every running session with long titles: half as much again as the lane takes whole.
        string row = $$"""{"session_id":"s","media_title":"{{new string('t', 1000)}}"}""";
        string rows = string.Join(",", Enumerable.Repeat(row, EventsLane.MaxFrameBytes * 3 / 2 / row.Length));
        await SendAsync(socket, $$"""{"type":"snapshot","channel":"sessions","timestamp":"2026-10-01T12:00:00Z","data":[{{rows}}]}""", token);
        await SendAsync(socket, """{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "e1", "timestamp": "t", "data": {} }""", token);
        await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        lock (frames)
        {
            Assert.Equal([EventsFrameTypes.Subscribed, EventsFrameTypes.Snapshot, EventsFrameTypes.Event], frames.Select(f => f.Type));
            Assert.Equal(("sessions", true), (frames[1].Channel, frames[1].IsCut));
            Assert.Equal(("catalog", false), (frames[2].Channel, frames[2].IsCut));
        }
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Single(_silo.Sent, r => r.Method == HttpMethod.Post);
        await lane.StopAsync();
    }

    [Fact]
    public async Task ASocketWithoutAHead_ClosesOnAFrameTooLarge()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = new WebSocketRealtimeSocket(maxFrameBytes: 8192);
        var connecting = client.ConnectAsync(new UriBuilder(_server) { Scheme = "ws", Path = "/x" }.Uri, ["p"], token);
        var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
        var accepted = await context.AcceptWebSocketAsync("p");
        using var socket = accepted.WebSocket;
        await connecting;

        await SendAsync(socket, new string('x', 10_000), token);

        Assert.Null(await client.ReceiveAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token));
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).WaitAsync(TimeSpan.FromSeconds(10), token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count)).RootElement.Clone();
    }

    private static Task SendAsync(WebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);
}
