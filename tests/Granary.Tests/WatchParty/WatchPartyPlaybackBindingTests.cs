using System.Text.Json;
using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.WatchParty;

/// <summary>
/// A player bound to a room, as Silo's Android app binds one: attach, reports, commands on the server's clock, the
/// readiness barrier, stalls, drift, and the viewer's controls as requests. The clock stands on 1 October 2026, 12:00 UTC;
/// the server's runs 10 s ahead.
/// </summary>
public sealed class WatchPartyPlaybackBindingTests : IDisposable
{
    private static readonly DateTimeOffset ServerStart = ManualTimeProvider.DefaultStart.AddSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly FakeRoomLink _room = new();
    private readonly FakeRoomPlayer _player = new();
    private readonly WatchPartyPlaybackBinding _binding;
    private readonly List<string> _notices = [];

    public WatchPartyPlaybackBindingTests()
    {
        _room.Room = Room();
        _player.ServerNow = () => _time.GetUtcNow().AddSeconds(10);
        _binding = new WatchPartyPlaybackBinding(_room, _player, new ImmediateDispatcher(), _time, NullLogger.Instance);
        _binding.Notice += (_, text) => _notices.Add(text);
        _binding.Start();
    }

    private static WatchPartyRoom Room(string state = "playing", long generation = 3, string role = "host", string? attached = "s1",
        bool selfReady = false, bool selfBuffering = false, bool ignoreWait = false, string policy = "host_only") => new()
    {
        RoomId = "r1",
        Phase = WatchPartyPhases.Playing,
        PlaybackState = state,
        SelfRole = role,
        SelectionRevision = 1,
        Generation = generation,
        AttachedSessionId = attached,
        SelfIgnoreWait = ignoreWait,
        GuestControlPolicy = policy,
        SelfCanControlTransport = role == "host" || policy == WatchPartyPolicies.GuestPlayPause,
        Members = [new WatchPartyMember { IsSelf = true, IsHost = role == "host", IsReady = selfReady, IsBuffering = selfBuffering }],
    };

    private static WatchPartyCommand Command(string id, string action, double position, double serverSecondsFromStart, string state = "playing",
        string? session = null) =>
        new(id, session, 1, action, position, ServerStart.AddSeconds(serverSecondsFromStart), state);

    private void Advance(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private List<JsonElement> Frames(string type) =>
        [.. _room.Sent.Select(text => JsonDocument.Parse(text).RootElement).Where(f => f.GetProperty("type").GetString() == type)];

    [Fact]
    public void TheSession_IsAttached_AndReportedOnlyOnceTheServerEchoedIt()
    {
        _room.Room = Room(attached: null);

        Advance(0.25);
        Assert.Equal("s1", Assert.Single(Frames("attach_session")).GetProperty("session_id").GetString());
        Advance(2);
        Assert.Empty(Frames("state_report"));

        _room.Room = Room();
        Advance(1.5);
        var report = Assert.Single(Frames("state_report"));
        Assert.Equal((12.0, false), (report.GetProperty("position_seconds").GetDouble(), report.GetProperty("is_paused").GetBoolean()));
        Advance(1.5);
        Assert.Equal(2, Frames("state_report").Count);
    }

    [Fact]
    public void ACommand_RunsAtItsExecuteAt_OnTheServersClock()
    {
        _player.Observation = _player.Observation with { IntendsToPlay = false, IsAdvancing = false };
        _room.Deliver(Command("c1", "play", 12, serverSecondsFromStart: 2));

        Advance(1.5);
        Assert.Empty(_player.Playing);
        Advance(0.5);

        Assert.Equal([true], _player.Playing);
        Assert.Empty(_player.Seeks);
    }

    [Fact]
    public void ALateCommand_LandsWhereTheRoomIsNow()
    {
        _player.Observation = _player.Observation with { PositionSeconds = 50, IntendsToPlay = false, IsAdvancing = false };
        // It should have run 3 s ago at 100 s: the room is at 103 s now.
        _room.Deliver(Command("c1", "play", 100, serverSecondsFromStart: -3));
        Advance(0.01);

        Assert.Equal(103, Assert.Single(_player.Seeks), 2);
        Assert.Equal([true], _player.Playing);
    }

    [Fact]
    public void AWaitingSeek_IsAcknowledged_UntilTheRoomCountsThisMemberReady()
    {
        _room.Room = Room(state: "waiting");
        _room.Deliver(Command("c1", "seek", 600, 0, state: "waiting"));
        Advance(0.01);
        Assert.Equal(600, Assert.Single(_player.Seeks), 2);
        _player.Observation = _player.Observation with { PositionSeconds = 600.2, IntendsToPlay = false, IsAdvancing = false };

        Advance(0.25);
        var ready = Assert.Single(Frames("ready"));
        Assert.Equal("c1", ready.GetProperty("command_id").GetString());
        Assert.True(Frames("state_report").Single().GetProperty("is_ready").GetBoolean());
        Advance(0.5);
        Assert.Equal(2, Frames("ready").Count);

        _room.Room = Room(state: "waiting", generation: 4, selfReady: true);
        Advance(1);
        Assert.Equal(2, Frames("ready").Count);
    }

    [Fact]
    public void AGuestFarFromTheTarget_DoesNotAcknowledge()
    {
        _room.Room = Room(state: "waiting", role: "guest");
        _room.Deliver(Command("c1", "seek", 600, 0, state: "waiting"));
        Advance(0.01);
        _player.Observation = _player.Observation with { PositionSeconds = 590, IntendsToPlay = false, IsAdvancing = false };

        Advance(1);

        Assert.Empty(Frames("ready"));
    }

    [Fact]
    public void SmallDrift_IsClosedByARate_ThatGoesBackTo1x()
    {
        _room.Deliver(Command("c1", "play", 12, 0));
        Advance(0.01);
        Assert.Empty(_player.Seeks);
        // The room plays on from 12 s at 12:00:10 server time; this player is 1 s behind it.
        _player.Following = true;
        _player.Lag = 1;

        Advance(1.5);
        Assert.Equal(1.04, _player.Rates[^1]!.Value, 3);

        _player.Lag = 0.05;
        Advance(0.5);
        Assert.Null(_player.Rates[^1]);
    }

    [Fact]
    public void TheViewersControls_AreRequests_AndAGuestsAreRefused()
    {
        Assert.Equal(RoomTransportResult.Sent, _binding.RequestSeek(90));
        var request = Assert.Single(Frames("transport_request"));
        Assert.Equal(("seek", 90.0), (request.GetProperty("action").GetString(), request.GetProperty("position_seconds").GetDouble()));
        Assert.Equal(90, _binding.PendingSeekSeconds);
        Assert.Empty(_player.Seeks);

        _room.Deliver(Command("c1", "seek", 90, 0, state: "waiting"));
        Advance(0.01);
        Assert.Null(_binding.PendingSeekSeconds);

        _room.Room = Room(role: "guest");
        Assert.Equal(RoomTransportResult.Denied, _binding.RequestSeek(10));
        Assert.Equal(RoomTransportResult.Denied, _binding.RequestPlayPause(pause: true));
        Assert.Equal([WatchPartyPlaybackBinding.HostSeeksText, WatchPartyPlaybackBinding.HostControlsText], _notices);

        _room.Room = Room(role: "guest", policy: WatchPartyPolicies.GuestPlayPause);
        Assert.Equal(RoomTransportResult.Sent, _binding.RequestPlayPause(pause: true));
        _room.IsConnected = false;
        Assert.Equal(RoomTransportResult.Reconnecting, _binding.RequestPlayPause(pause: false));
    }

    [Fact]
    public void AStall_IsReportedOnce_AfterTwoSeconds()
    {
        _player.Observation = _player.Observation with { IsBuffering = true, IsAdvancing = false };

        Advance(1.75);
        Assert.Empty(Frames("buffering"));
        Advance(0.5);
        Assert.Single(Frames("buffering"));
        Advance(3);
        Assert.Single(Frames("buffering"));
        Assert.Empty(Frames("state_report"));
    }

    [Fact]
    public void CatchingUp_AcknowledgesRecovery()
    {
        _room.Room = Room(ignoreWait: true);

        Advance(0.25);

        Assert.Single(Frames("ready"));
        Assert.True(_binding.IsCatchingUp);
    }

    [Fact]
    public void CommandsOfAnOlderConnection_OrAnotherSession_AreDropped()
    {
        _room.Deliver(Command("c1", "pause", 12, 0), epoch: 0);
        _room.Deliver(Command("c2", "pause", 12, 0, session: "other"));
        Advance(1);

        Assert.Empty(_player.Playing);
    }

    [Fact]
    public void ARoomBackInTheLobby_EndsTheCorrection()
    {
        _room.Deliver(Command("c1", "play", 12, 0));
        Advance(0.01);
        _player.Following = true;
        _player.Lag = 1;
        Advance(1.5);
        Assert.NotNull(_player.Rates[^1]);

        _room.Room = Room() with { Phase = WatchPartyPhases.Lobby };
        Advance(0.25);

        Assert.Null(_player.Rates[^1]);
        int sent = _room.Sent.Count;
        Advance(3);
        Assert.Equal(sent, _room.Sent.Count);
    }

    public void Dispose() => _binding.Dispose();

    private sealed class FakeRoomLink : IRoomLink
    {
        public WatchPartyRoom? Room { get; set; }

        public bool IsConnected { get; set; } = true;

        public long ConnectionEpoch { get; set; } = 1;

        public double? ClockOffsetMs { get; set; } = 10_000;

        public List<string> Sent { get; } = [];

        public event EventHandler<WatchPartyDelivery>? CommandReceived;

        public Task<bool> SendAsync(string frame)
        {
            Sent.Add(frame);
            return Task.FromResult(IsConnected);
        }

        public void Deliver(WatchPartyCommand command, long? epoch = null) =>
            CommandReceived?.Invoke(this, new WatchPartyDelivery(command, epoch ?? ConnectionEpoch));
    }

    private sealed class FakeRoomPlayer : IRoomPlayer
    {
        private readonly DateTimeOffset _since = ServerStart;

        public RoomPlayerObservation Observation { get; set; } = new("s1", 12, IntendsToPlay: true, IsAdvancing: true, IsBuffering: false,
            IsBusy: false, IsEnded: false);

        /// <summary>The player plays along with the room from 12 s at 12:00:10 server time, <see cref="Lag"/> behind.</summary>
        public bool Following { get; set; }

        public double Lag { get; set; }

        public Func<DateTimeOffset>? ServerNow { get; set; }

        public List<double> Seeks { get; } = [];

        public List<bool> Playing { get; } = [];

        public List<double?> Rates { get; } = [];

        public RoomPlayerObservation Observe()
        {
            if (!Following || ServerNow is null)
            {
                return Observation;
            }
            return Observation with { PositionSeconds = 12 + (ServerNow() - _since).TotalSeconds - Lag };
        }

        public void SeekTo(double sourceSeconds) => Seeks.Add(sourceSeconds);

        public void SetPlaying(bool playing) => Playing.Add(playing);

        public void SetCorrectionRate(double? rate) => Rates.Add(rate);

        public bool CanReachInPlace(double sourceSeconds) => true;
    }
}
