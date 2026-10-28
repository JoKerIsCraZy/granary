using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The player's Watch Party panel, in the words of the web's <c>WatchTogetherPanel</c>.</summary>
public sealed class WatchPartyPanelTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly RecordingNavigation _navigation = new();
    private readonly WatchPartySession _party;
    private readonly List<PlaybackNotice> _said = [];

    public WatchPartyPanelTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        var api = new WatchPartyApi(_silo.Http);
        _party = new WatchPartySession(_session, api, () => new WatchPartyConnection(api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>());
    }

    private async Task<WatchPartyPanelViewModel> PanelAsync(string room)
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        var panel = new WatchPartyPanelViewModel(_party, _navigation, _logs.For<WatchPartyPanelViewModel>());
        panel.Said += (_, notice) => _said.Add(notice);
        panel.Attach();
        return panel;
    }

    private static string Playing(string role = "host", string policy = "host_only", bool transport = true, int count = 2, string? invite = "/rooms/join?token=invite-secret",
        string elena = "", string maya = "", long generation = 3) =>
        WatchPartyPayloads.Room("playing", "playing", generation: generation, revision: 1, role: role, selected: "movie:heat", policy: policy, transport: transport,
            count: count, invite: invite, elena: elena, maya: maya);

    private string LastSaid => _said.Count > 0 ? _said[^1].Message : "";

    [Fact]
    public async Task TheHost_SeesTheCode_TheViewers_AndThatOnlyTheyControlPlayback()
    {
        var panel = await PanelAsync(Playing());

        Assert.Equal("ABCD1234", panel.Code);
        Assert.Equal("2 viewers", panel.ViewersText);
        Assert.Equal("Only you control playback", panel.PolicyText);
        Assert.True(panel.IsHost);
        Assert.Equal("Allow Pause", panel.PolicyButtonText);
        Assert.Equal("Connected", panel.ConnectionText);
        Assert.Equal(WatchPartyConnectionState.Connected, panel.ConnectionState);
    }

    [Fact]
    public async Task OneViewer_IsOneViewer_AndGuestsWhoMayPause_AreNamed()
    {
        var panel = await PanelAsync(Playing(policy: "guest_play_pause", count: 1));

        Assert.Equal("1 viewer", panel.ViewersText);
        Assert.Equal("Guests can pause & resume", panel.PolicyText);
        Assert.Equal("Host Only", panel.PolicyButtonText);
    }

    [Fact]
    public async Task AGuest_IsToldWhoControlsPlayback_AndHasNoHostButtons()
    {
        var panel = await PanelAsync(Playing(role: "guest", transport: false));

        Assert.False(panel.IsHost);
        Assert.Equal("Host controls playback", panel.PolicyText);
        Assert.False(panel.StopCommand.CanExecute(null));
        Assert.False(panel.TogglePolicyCommand.CanExecute(null));

        _sockets[0].Push(WatchPartyPayloads.Snapshot(Playing(role: "guest", policy: "guest_play_pause", transport: true, generation: 4)));
        await ServerPlaybackFixture.Until(() => panel.PolicyText == "You can pause & resume", "the guest's new policy");
    }

    [Fact]
    public async Task EachViewer_ShowsWhatTheyAreDoing_AndTheViewerIsYou()
    {
        var panel = await PanelAsync(Playing(elena: "\"is_ready\": true", maya: "\"is_buffering\": true"));

        Assert.Equal([("Elena (you)", "Ready"), ("Maya", "Buffering")], panel.Viewers.Select(v => (v.Name, v.State)));

        _sockets[0].Push(WatchPartyPayloads.Snapshot(Playing(maya: "\"is_syncing\": true", generation: 4)));
        await ServerPlaybackFixture.Until(() => panel.Viewers.Count == 2 && panel.Viewers[1].State == "Syncing", "Maya syncing");
        Assert.Equal("Connected", panel.Viewers[0].State);
    }

    [Fact]
    public async Task TheInvite_IsCopiedOnlyOnceTheLinkIsThere()
    {
        var panel = await PanelAsync(Playing(invite: null));
        var copied = new List<string>();

        panel.CopyInvite(text =>
        {
            copied.Add(text);
            return true;
        });
        Assert.Empty(copied);
        Assert.Equal("Invite link is not ready yet.", LastSaid);

        _sockets[0].Push(WatchPartyPayloads.Snapshot(Playing(generation: 4)));
        await ServerPlaybackFixture.Until(() => _party.InviteLink is not null, "the invite link");
        panel.CopyInvite(text =>
        {
            copied.Add(text);
            return true;
        });

        Assert.Equal(["https://nas.example/silo/rooms/join?token=invite-secret"], copied);
        Assert.Equal("Invite copied. Room code ABCD1234", LastSaid);

        panel.CopyInvite(_ => false);
        Assert.Equal("Failed to copy invite link", LastSaid);
        Assert.True(_said[^1].IsWarning);
    }

    [Fact]
    public async Task AllowPause_FlipsThePolicy_AndSaysSo()
    {
        var panel = await PanelAsync(Playing());
        _silo.On(HttpMethod.Patch, Room + "/policy",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(Playing(policy: "guest_play_pause", generation: 4))));

        await panel.TogglePolicyCommand.ExecuteAsync(null);

        Assert.Contains("\"guest_play_pause\"", _silo.Sent.Last(r => r.Method == HttpMethod.Patch).Body, StringComparison.Ordinal);
        Assert.Equal("Guests can now pause and resume", LastSaid);
        Assert.Equal("Guests can pause & resume", panel.PolicyText);
        Assert.Equal("Host Only", panel.PolicyButtonText);

        _silo.On(HttpMethod.Patch, Room + "/policy", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(Playing(generation: 5))));
        await panel.TogglePolicyCommand.ExecuteAsync(null);

        Assert.Contains("\"host_only\"", _silo.Sent.Last(r => r.Method == HttpMethod.Patch).Body, StringComparison.Ordinal);
        Assert.Equal("Room is now host controlled", LastSaid);
    }

    [Fact]
    public async Task Stop_StopsForEveryone_AndAFailureSaysWhatFailed()
    {
        var panel = await PanelAsync(Playing());
        _silo.On(HttpMethod.Post, Room + "/playback/stop", ScriptedSilo.NetworkError());

        await panel.StopCommand.ExecuteAsync(null);

        Assert.Equal("Could not stop playback. Couldn't reach the server. Check the connection and try again.", LastSaid);
        Assert.True(_said[^1].IsWarning);
        Assert.True(panel.StopCommand.CanExecute(null));

        _silo.On(HttpMethod.Post, Room + "/playback/stop", ScriptedSilo.Problem(409, "conflict"));
        await panel.StopCommand.ExecuteAsync(null);
        Assert.Equal("Could not stop playback", LastSaid);

        _silo.On(HttpMethod.Post, Room + "/playback/stop", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 4, revision: 1, selected: "movie:heat"))));
        await panel.StopCommand.ExecuteAsync(null);

        Assert.Equal("Stopped for everyone. The room is still open.", LastSaid);
        Assert.False(_party.Room!.IsPlaying);
    }

    [Fact]
    public async Task End_EndsTheRoomForEveryone_AndGoesToWatchParty()
    {
        var panel = await PanelAsync(Playing());
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.Problem(500, "internal"));

        await panel.EndRoomAsync();

        Assert.Equal("Failed to end room", LastSaid);
        Assert.True(_party.IsInRoom);
        Assert.DoesNotContain(_navigation.Navigations, n => n.Route == AppRoute.WatchParty);

        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.NoContent());
        await panel.EndRoomAsync();

        Assert.False(_party.IsInRoom);
        Assert.Equal(AppRoute.WatchParty, _navigation.Navigations[^1].Route);
    }

    [Fact]
    public async Task ADetachedPanel_NoLongerFollowsTheRoom()
    {
        var panel = await PanelAsync(Playing());
        panel.Detach();

        _sockets[0].Push(WatchPartyPayloads.Snapshot(Playing(count: 3, generation: 4)));
        await ServerPlaybackFixture.Until(() => _party.Room?.MemberCount == 3, "the new snapshot");

        Assert.Equal("2 viewers", panel.ViewersText);
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
