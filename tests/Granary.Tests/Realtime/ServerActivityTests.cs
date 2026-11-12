using Granary.Core.Auth;
using Granary.Core.Realtime;
using Granary.Tests.Playback;

namespace Granary.Tests.Realtime;

/// <summary>
/// The server's activity for the acting administrator, as the web's indicator reads it: the server's answer to the
/// subscription decides whether there is one, and it starts over with every other server, profile or sign-out.
/// </summary>
public sealed class ServerActivityTests : IAsyncDisposable
{
    private readonly ServerActivityHarness _harness = new();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task AnAdministratorsConnection_AsksForTheAdministratorsChannelsToo()
    {
        var socket = await _harness.StartedAsync([.. EventsChannels.Admin]);

        Assert.Contains("\"channels\":[\"catalog\",\"user_state\",\"notifications\",\"sessions\",\"tasks\",\"scans\",\"download_preparations\"]",
            Assert.Single(socket.Sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAProfileTheServerOffersNoAdministratorsChannel_NoneIsAskedFor_AndNothingIsTurnedDown()
    {
        // An administrator account on a profile that is not its primary one: the hello offers everyone's channels only.
        var socket = await _harness.StartedAsync();

        Assert.Contains("\"channels\":[\"catalog\",\"user_state\",\"notifications\"]", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.False(_harness.State.IsAvailable);
        Assert.DoesNotContain(_harness.Library.Logs.Lines, line => line.Contains("turned down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerWhoseHelloNamesNoChannels_IsAskedForAll_AndItsAnswerDecides()
    {
        _harness.Events.Start();
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 1, "the connection");
        var socket = _harness.Socket(0);
        socket.Push("""{ "type": "hello", "schema_version": 1, "connection_id": "c1", "required_action": "subscribe" }""");
        await ServerPlaybackFixture.Until(() => !socket.Sent.IsEmpty, "the subscribe");

        await _harness.PushedAsync(socket, ServerActivityHarness.Subscribed([EventsChannels.Scans],
            [EventsChannels.Sessions, EventsChannels.Tasks, EventsChannels.DownloadPreparations]));

        Assert.Contains("\"sessions\",\"tasks\",\"scans\",\"download_preparations\"", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.Equal([EventsChannels.Scans], _harness.State.Channels.Order());
    }

    [Fact]
    public async Task TheChannelsTheServerGave_AreWhatThereIsToShow()
    {
        Assert.False(_harness.State.IsAvailable);

        await _harness.StartedAsync(EventsChannels.Sessions, EventsChannels.Scans);

        Assert.True(_harness.State.IsAvailable);
        Assert.Equal([EventsChannels.Scans, EventsChannels.Sessions], _harness.State.Channels.Order());
    }

    [Fact]
    public async Task AProfileTheServerTurnsDown_HasNoIndicator()
    {
        // An administrator account on a profile that is not its primary one: every administrator's channel is forbidden.
        await _harness.StartedAsync();

        Assert.False(_harness.State.IsAvailable);
        Assert.Equal(EventsConnection.Live, _harness.State.Connection);
    }

    [Fact]
    public async Task ChannelsThatAreNotTheAdministrators_DoNotMakeAnIndicator()
    {
        _harness.Events.Start();
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 1, "the connection");

        await _harness.PushedAsync(_harness.Socket(0), ServerActivityHarness.Subscribed(["catalog", "user_state", "notifications", "jobs"]));

        Assert.False(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task TheConnectionsState_IsKept()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        Assert.Equal(EventsConnection.Live, _harness.State.Connection);

        socket.Close();
        await ServerPlaybackFixture.Until(() => _harness.State.Connection == EventsConnection.Disconnected, "the lost connection");

        // What the server gave stays while the next connection is made.
        Assert.True(_harness.State.IsAvailable);
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(1), "the wait before the next connection");
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 2 && _harness.State.Connection == EventsConnection.Live, "the next connection");
    }

    [Fact]
    public async Task AnAccessChange_HidesIt_UntilTheNextConnectionSaysWhatMayBeSeen()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(2));

        await _harness.PushedAsync(socket, """{ "type": "access_changed" }""");
        Assert.False(_harness.State.IsAvailable);

        socket.Close(4001);
        await _harness.OpenedAsync(1, EventsChannels.Sessions);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AnotherProfile_StartsOver_AndTheLastOnesFramesAreDropped()
    {
        var first = await _harness.StartedAsync(EventsChannels.Sessions);
        int changes = 0;
        _harness.Activity.Changed += (_, _) => Interlocked.Increment(ref changes);

        _harness.Library.Session.ProfileId = "8";
        _harness.Library.Session.RaiseChanged();

        Assert.False(_harness.State.IsAvailable);
        Assert.True(Volatile.Read(ref changes) >= 1);
        // The replaced connection's number is no longer the activity's.
        _harness.Activity.Handle(1, EventsFrames.Parse(ServerActivityHarness.Subscribed([EventsChannels.Sessions]))!);
        Assert.False(_harness.State.IsAvailable);
        await ServerPlaybackFixture.Until(() => first.Disposed, "the first connection closed");
    }

    [Fact]
    public async Task SigningOut_ClearsIt()
    {
        await _harness.StartedAsync(EventsChannels.Sessions, EventsChannels.Tasks);

        _harness.Library.Session.State = SessionState.SignedOut;
        _harness.Library.Session.RaiseChanged();

        Assert.Same(ServerActivityState.None, _harness.State);
    }

    [Fact]
    public async Task Stopping_ClearsIt()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);

        await _harness.Events.StopAsync();

        Assert.Same(ServerActivityState.None, _harness.State);
    }

    [Fact]
    public async Task AnAccountThatIsNoAdministrator_AsksForNothingMore_AndHasNoIndicator()
    {
        _harness.Library.Session.Account = ServerActivityHarness.Administrator with { Role = "user" };
        _harness.Events.Start();
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 1, "the connection");
        var socket = _harness.Socket(0);
        socket.Push(ServerActivityHarness.Hello);
        await ServerPlaybackFixture.Until(() => !socket.Sent.IsEmpty, "the subscribe");

        // Even a server that gave such a channel unasked would not make one.
        await _harness.PushedAsync(socket, ServerActivityHarness.Subscribed([EventsChannels.Sessions]));

        Assert.Contains("\"channels\":[\"catalog\",\"user_state\",\"notifications\"]", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.Same(ServerActivityState.None, _harness.State);
    }

    [Fact]
    public async Task AnAccountMadeAnAdministrator_GetsAConnectionThatAsksForItsChannels()
    {
        _harness.Library.Session.Account = ServerActivityHarness.Administrator with { Role = "user" };
        _harness.Events.Start();
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 1, "the connection");

        _harness.Library.Session.Account = ServerActivityHarness.Administrator;
        _harness.Library.Session.RaiseChanged();

        var socket = await _harness.OpenedAsync(1, EventsChannels.Sessions);
        Assert.Contains("\"sessions\"", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.True(_harness.Socket(0).Disposed);
        Assert.True(_harness.State.IsAvailable);
    }
}
