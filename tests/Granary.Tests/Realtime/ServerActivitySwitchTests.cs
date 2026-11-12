using Granary.Core.Realtime;
using Granary.Tests.Admin;
using Granary.Tests.Playback;

namespace Granary.Tests.Realtime;

/// <summary>
/// The events stream under the indicator's switch in Settings (<see cref="ActivityIndicatorSetting"/>). Off, an
/// administrator's connection asks for what everyone's asks for and nothing of the server's activity is read; the
/// connection itself stays, for Home and the notification count. Switching replaces it by one that asks for the other
/// channels, and costs the profile none of its own news.
/// </summary>
public sealed class ServerActivitySwitchTests : IAsyncDisposable
{
    private const string SessionsPath = "api/v2/admin/sessions";
    private const string EveryonesChannels = "\"channels\":[\"catalog\",\"user_state\",\"notifications\"]";
    private const string CatalogChanged =
        """{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "c1", "timestamp": "t", "data": { "content_id": "movie:heat" } }""";

    private readonly ServerActivityHarness _harness = new();

    public ServerActivitySwitchTests()
    {
        _harness.Library.Answer(SessionsPath, AdminPayloads.Sessions);
    }

    private int Reads => _harness.Library.Count(SessionsPath);

    private long Version => _harness.Library.ViewerState.Version;

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private Task ShownAsync(int streams) =>
        ServerPlaybackFixture.Until(() => _harness.State.Sessions?.Count == streams, $"{streams} streams");

    [Fact]
    public async Task SwitchedOff_AnAdministratorsConnection_AsksForEveryonesChannelsOnly_AndReadsNothing()
    {
        await _harness.Indicator.SetShownAsync(false);
        _harness.Events.Start();
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 1, "the connection");
        var socket = _harness.Socket(0);
        socket.Push(ServerActivityHarness.HelloOffering(EventsChannels.Admin));
        await ServerPlaybackFixture.Until(() => !socket.Sent.IsEmpty, "the subscribe");

        // Even a server that gave such a channel unasked would not make an indicator.
        await _harness.PushedAsync(socket, ServerActivityHarness.Subscribed([EventsChannels.Sessions]));

        Assert.Contains(EveryonesChannels, Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.Same(ServerActivityState.None, _harness.State);
        Assert.Equal(0, Reads);
    }

    [Fact]
    public async Task SwitchingItOff_LeavesNothingOfTheActivity_AndAConnectionWithoutTheAdministratorsChannels()
    {
        var first = await _harness.StartedAsync(EventsChannels.Sessions);
        await ShownAsync(2);

        await _harness.Indicator.SetShownAsync(false);

        Assert.Same(ServerActivityState.None, _harness.State);
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 2, "the next connection");
        var second = _harness.Socket(1);
        second.Push(ServerActivityHarness.HelloOffering(EventsChannels.Admin));
        await ServerPlaybackFixture.Until(() => !second.Sent.IsEmpty, "the subscribe");
        Assert.Contains(EveryonesChannels, Assert.Single(second.Sent), StringComparison.Ordinal);
        await ServerPlaybackFixture.Until(() => first.Disposed, "the first connection closed");
        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task SwitchedOnAgain_TheConnectionAsksForTheAdministratorsChannels_AndTheActivityIsBack()
    {
        await _harness.Indicator.SetShownAsync(false);
        await _harness.StartedAsync();
        Assert.False(_harness.State.IsAvailable);

        await _harness.Indicator.SetShownAsync(true);

        var socket = await _harness.OpenedAsync(1, EventsChannels.Sessions);
        Assert.Contains("\"sessions\"", Assert.Single(socket.Sent), StringComparison.Ordinal);
        await ShownAsync(2);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task AProfileThatLeftItsSwitchOn_KeepsItsActivity_BesideOneThatTurnedItOff()
    {
        await _harness.Indicator.SetShownAsync(false);
        await _harness.StartedAsync();

        _harness.Library.Session.ProfileId = "8";
        _harness.Library.Session.RaiseChanged();

        var socket = await _harness.OpenedAsync(1, EventsChannels.Sessions);
        Assert.Contains("\"sessions\"", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.True(_harness.State.IsAvailable);
    }

    [Fact]
    public async Task SwitchingIt_KeepsAReloadThatWasWaiting()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);
        long before = Version;
        await _harness.PushedAsync(socket, CatalogChanged);

        await _harness.Indicator.SetShownAsync(false);
        _harness.Library.Clock.Advance(RealtimeEvents.SettleDelay);

        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task AConnectionThatComesAtOnceAfterTheSwitch_ReloadsNothing()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);
        long before = Version;

        await _harness.Indicator.SetShownAsync(false);
        await _harness.OpenedAsync(1);
        _harness.Library.Clock.Advance(RealtimeEvents.LongestSettle);

        Assert.Equal(before, Version);
    }

    [Fact]
    public async Task AConnectionThatTookAWhileAfterTheSwitch_ReloadsOnce_ForWhatWasNotSentMeanwhile()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);
        long before = Version;

        await _harness.Indicator.SetShownAsync(false);
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 2, "the next connection");
        _harness.Library.Clock.Advance(RealtimeEvents.CatchUpAfter + TimeSpan.FromSeconds(1));
        await _harness.OpenedAsync(1);
        _harness.Library.Clock.Advance(RealtimeEvents.SettleDelay);

        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task AFrameTheReplacedConnectionStillHandsOn_IsNotTakenForTheNewOnesStart()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);
        long before = Version;
        await _harness.Indicator.SetShownAsync(false);
        await ServerPlaybackFixture.Until(() => _harness.SocketCount == 2, "the next connection");

        // The connection that was replaced says a last "subscribed" after the switch, before the new one has said its own.
        _harness.Events.Handle(new EventsFrame(EventsFrameTypes.Subscribed), "7", _harness.Lanes[0]);
        _harness.Library.Clock.Advance(RealtimeEvents.CatchUpAfter + TimeSpan.FromSeconds(1));
        await _harness.OpenedAsync(1);
        _harness.Library.Clock.Advance(RealtimeEvents.SettleDelay);

        // The time since the switch still counts: what the server sent meanwhile is read once.
        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task Stopped_TheStreamNoLongerFollowsTheSwitch()
    {
        await _harness.StartedAsync(EventsChannels.Sessions);
        await _harness.Events.StopAsync();

        // The stream let go of the switch before its stop returned, so neither change reaches it.
        await _harness.Indicator.SetShownAsync(false);
        await _harness.Indicator.SetShownAsync(true);

        Assert.Equal(1, _harness.SocketCount);
    }
}
