using System.Net;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>What the player does with the server's and an administrator's commands, as Silo's web player does.</summary>
public sealed class ServerPlaybackControlTests
{
    private const string Session = ServerPlaybackFixture.Session;
    private const string TicketPath = "api/v2/playback/sessions/" + Session + "/control/ws-ticket";

    private readonly ServerPlaybackFixture _fixture = new();
    private readonly List<FakeControlSocket> _sockets = [];
    private readonly List<PlaybackNotice> _notices = [];

    public ServerPlaybackControlTests()
    {
        _fixture.Silo.On(HttpMethod.Post, TicketPath,
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "ticket": "t0k3n", "expires_in": 30, "max_connection_seconds": 14400, "protocol": "silo.playback-control.v2" }"""));
        _fixture.ControlLane = new PlaybackControlLane(new PlaybackControlApi(_fixture.Silo.Http), () =>
        {
            var socket = new FakeControlSocket();
            _sockets.Add(socket);
            return socket;
        }, _fixture.Time, _fixture.Logs.For<PlaybackControlLane>());
    }

    private FakeControlSocket Socket => _sockets[^1];

    private async Task<ServerPlayback> PlayingAsync()
    {
        var playback = await _fixture.PlayingAsync();
        playback.NoticeRaised += (_, notice) => _notices.Add(notice);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket.Sent.Count == 1, "the lane's hello");
        return playback;
    }

    // Sends a command and waits for the player's result.
    private async Task<JsonElement> SendAsync(string commandId, string name, string payload = "{}", string? issuedBy = "admin")
    {
        int answered = Socket.Sent.Count + 2;
        Socket.Push(FakeControlSocket.Command(Session, commandId, name, payload, issuedBy));
        await ServerPlaybackFixture.Until(() => Socket.Sent.Count >= answered, "the result of " + name);
        return JsonDocument.Parse(Socket.Sent.ToArray()[answered - 1]).RootElement;
    }

    [Fact]
    public async Task TheLane_OpensForTheSessionThatPlays()
    {
        await using var playback = await PlayingAsync();

        var mint = JsonDocument.Parse(Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, TicketPath)).Body!).RootElement;
        Assert.Equal(PlaybackPayloads.InstallationId, mint.GetProperty("installation_id").GetString());
        Assert.Contains(Session, Socket.Uri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseAndUnpause_MoveThePlayer()
    {
        await using var playback = await PlayingAsync();

        var paused = await SendAsync("c1", "pause");
        Assert.True(_fixture.Engine.Current.IsPaused);
        await SendAsync("c2", "unpause");
        Assert.False(_fixture.Engine.Current.IsPaused);

        Assert.Equal("completed", paused.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SeekAndVolume_TakeTheirPayload()
    {
        await using var playback = await PlayingAsync();

        await SendAsync("c1", "seek", """{ "position_seconds": 600 }""");
        await SendAsync("c2", "set_volume", """{ "volume": 0.4 }""");
        var noPosition = await SendAsync("c3", "seek", """{ "position": "600" }""");

        Assert.NotEmpty(_fixture.Engine.Seeks);
        Assert.Equal(40, _fixture.Engine.Current.Volume, 3);
        Assert.Equal(("rejected", "missing_seek_position"), (noPosition.GetProperty("status").GetString(), noPosition.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Messages_ShowAsNotices_InTheWebsWords()
    {
        await using var playback = await PlayingAsync();

        await SendAsync("c1", "display_message", """{ "message": "Server restart in 5 minutes." }""");
        await SendAsync("c2", "display_message", """{ "title": "Hello", "message": "Hi there" }""");
        await SendAsync("c3", "server_restarting", issuedBy: "server");

        Assert.Equal(
        [
            new PlaybackNotice("Playback notice", "Server restart in 5 minutes."),
            new PlaybackNotice("Hello", "Hi there"),
            new PlaybackNotice("Server restarting", "Playback may end shortly while the server restarts.", IsWarning: true),
        ], _notices);
    }

    [Theory]
    [InlineData("terminate", "{}", "admin", ServerPlayback.AdminEndedText)]
    [InlineData("stop", "{}", "admin", ServerPlayback.AdminStoppedText)]
    [InlineData("terminate", """{ "message": "Maintenance tonight." }""", "admin", "Maintenance tonight.")]
    [InlineData("stop", "{}", "server", "The server ended this playback session.")]
    public async Task StopAndTerminate_EndPlayback_AfterTheAnswer(string name, string payload, string issuedBy, string expected)
    {
        await using var playback = await PlayingAsync();

        var result = await SendAsync("c1", name, payload, issuedBy);
        await ServerPlaybackFixture.Until(() => playback.Status.Phase == ServerPlaybackPhase.Failed, "the end");

        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, expected, CanRetry: false), playback.Status);
        Assert.True(_fixture.Engine.Stops > 0);
    }

    [Fact]
    public async Task PauseAndSeek_BeforeThePictureShows_AreRefused()
    {
        await using var playback = await _fixture.StartAsync();
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket.Sent.Count == 1, "the lane's hello");

        var pause = await SendAsync("c1", "pause");
        var seek = await SendAsync("c2", "seek", """{ "position_seconds": 60 }""");
        var message = await SendAsync("c3", "display_message", """{ "message": "Hi" }""");

        Assert.Equal(("rejected", "not_playing"), (pause.GetProperty("status").GetString(), pause.GetProperty("error").GetString()));
        Assert.Equal("rejected", seek.GetProperty("status").GetString());
        Assert.Equal("completed", message.GetProperty("status").GetString());
        Assert.False(_fixture.Engine.Current.IsPaused);
    }

    [Fact]
    public async Task UnknownCommand_IsRejected()
    {
        await using var playback = await PlayingAsync();

        var result = await SendAsync("c1", "play_media");

        Assert.Equal(("rejected", "unsupported"), (result.GetProperty("status").GetString(), result.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task LeavingThePlayer_ClosesTheLane()
    {
        var playback = await PlayingAsync();

        await playback.StopAsync();
        await playback.DisposeAsync();

        Assert.True(Socket.Disposed);
    }

    [Fact]
    public async Task SessionEndedOnTheServer_SaysAnAdministratorEndedIt()
    {
        _fixture.Silo.On(HttpMethod.Post, ServerPlaybackFixture.ProgressPath, ScriptedSilo.Problem(410, "playback_session_ended"));
        await using var playback = await PlayingAsync();

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        await ServerPlaybackFixture.Until(() => playback.Status.Phase == ServerPlaybackPhase.Failed, "the end");

        Assert.Equal(ServerPlayback.AdminEndedText, playback.Status.Message);
    }
}
