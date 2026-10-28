using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.WatchParty;
using Granary.Core.Realtime;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>
/// The room connection over a real WebSocket: a local server takes the upgrade as Silo's does (the room protocol chosen,
/// the ticket offered beside it), the client pings, and a snapshot from the server comes through.
/// </summary>
public sealed class WatchPartySocketTests : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Uri _server;
    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    public WatchPartySocketTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server = new Uri($"http://localhost:{port}/");
        _listener.Prefixes.Add(_server.AbsoluteUri);
        _listener.Start();
        _silo.On(HttpMethod.Post, new Uri(_server, $"api/v2/watch-together/rooms/{WatchPartyPayloads.RoomId}/ws-ticket"),
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
    }

    [Fact]
    public async Task TheRoom_TalksTheProtocol_OverARealSocket()
    {
        var token = TestContext.Current.CancellationToken;
        var snapshot = new TaskCompletionSource<WatchPartyRoom>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new WatchPartyConnection(new WatchPartyApi(_silo.Http), () => new WebSocketRealtimeSocket(),
            TimeProvider.System, _logs.For<WatchPartyConnection>());
        connection.FrameReceived += (_, frame) =>
        {
            if (frame is WatchPartyFrame.Snapshot { Room: var room })
            {
                snapshot.TrySetResult(room);
            }
        };

        connection.Open(_server, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token);
        var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(context.Request.IsWebSocketRequest);
        Assert.Equal($"/api/v2/watch-together/rooms/{WatchPartyPayloads.RoomId}/ws", context.Request.Url!.AbsolutePath);
        Assert.Equal("silo.room.v2, silo.ticket." + WatchPartyPayloads.Ticket, context.Request.Headers["Sec-WebSocket-Protocol"]);
        Assert.Null(context.Request.Headers["X-Room-Token"]);
        Assert.Equal("", context.Request.Url.Query);
        var accepted = await context.AcceptWebSocketAsync("silo.room.v2");
        using var socket = accepted.WebSocket;

        var ping = await ReceiveAsync(socket, token);
        Assert.Equal("ping", ping.GetProperty("type").GetString());
        await SendAsync(socket, WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 9)), token);

        Assert.Equal(9, (await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5), token)).Generation);
        await connection.CloseAsync();
        var closing = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), token).WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(WebSocketMessageType.Close, closing.MessageType);
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
