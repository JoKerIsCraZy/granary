using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.Realtime;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// The lane over a real WebSocket: a local server takes the upgrade as Silo's does (the protocol chosen, the ticket offered
/// beside it), and the player says hello, then answers a command with ack and result.
/// </summary>
public sealed class PlaybackControlSocketTests : IAsyncDisposable
{
    private const string Session = PlaybackPayloads.SessionId;

    private readonly HttpListener _listener = new();
    private readonly Uri _server;
    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    public PlaybackControlSocketTests()
    {
        int port = FreePort();
        _server = new Uri($"http://localhost:{port}/");
        _listener.Prefixes.Add(_server.AbsoluteUri);
        _listener.Start();
        _silo.On(HttpMethod.Post, new Uri(_server, $"api/v2/playback/sessions/{Session}/control/ws-ticket"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "ticket": "Tk_42", "expires_in": 30, "max_connection_seconds": 60, "protocol": "silo.playback-control.v2" }"""));
    }

    [Fact]
    public async Task Lane_TalksTheProtocol_OverARealSocket()
    {
        var token = TestContext.Current.CancellationToken;
        var carried = new TaskCompletionSource<PlaybackControlCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lane = new PlaybackControlLane(new PlaybackControlApi(_silo.Http), () => new WebSocketRealtimeSocket(), TimeProvider.System,
            _logs.For<PlaybackControlLane>());

        lane.Follow(_server, Session, PlaybackPayloads.InstallationId, command =>
        {
            carried.TrySetResult(command);
            return Task.FromResult(PlaybackControlReply.Done);
        });
        var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(context.Request.IsWebSocketRequest);
        Assert.Equal($"/api/v2/playback/sessions/{Session}/control/ws", context.Request.Url!.AbsolutePath);
        Assert.Equal("silo.playback-control.v2, silo.ticket.Tk_42", context.Request.Headers["Sec-WebSocket-Protocol"]);
        Assert.Null(context.Request.Headers["Authorization"]);
        var accepted = await context.AcceptWebSocketAsync("silo.playback-control.v2");
        using var socket = accepted.WebSocket;

        var hello = await ReceiveAsync(socket, token);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        await SendAsync(socket, $$"""{ "type": "command", "command_id": "c1", "session_id": "{{Session}}", "name": "display_message", "payload": { "message": "Hi" }, "issued_by": { "kind": "admin" } }""", token);
        var ack = await ReceiveAsync(socket, token);
        var result = await ReceiveAsync(socket, token);

        Assert.Equal(("ack", "c1"), (ack.GetProperty("type").GetString(), ack.GetProperty("command_id").GetString()));
        Assert.Equal(("result", "completed"), (result.GetProperty("type").GetString(), result.GetProperty("status").GetString()));
        Assert.Equal("Hi", (await carried.Task.WaitAsync(TimeSpan.FromSeconds(5), token)).Text("message"));

        await lane.StopAsync();
        var closing = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]), token).WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(WebSocketMessageType.Close, closing.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, closing.CloseStatus);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
