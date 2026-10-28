using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>What the player does now, in the title's own time.</summary>
/// <param name="SessionId">The server playback session that plays; null until one does.</param>
/// <param name="IntendsToPlay">Not paused: it plays, or will once it has data.</param>
/// <param name="IsAdvancing">It actually plays.</param>
/// <param name="IsBuffering">It waits for data.</param>
/// <param name="IsBusy">A load, seek or new stream is under way: its position is no decision.</param>
public sealed record RoomPlayerObservation(string? SessionId, double PositionSeconds, bool IntendsToPlay, bool IsAdvancing, bool IsBuffering,
    bool IsBusy, bool IsEnded);

/// <summary>The player side of a Watch Party: what the room's commands act on.</summary>
public interface IRoomPlayer
{
    RoomPlayerObservation Observe();

    /// <summary>A room seek: in the stream when it can, with a new one otherwise.</summary>
    void SeekTo(double sourceSeconds);

    /// <summary>The room's play or pause; never echoed back as the viewer's.</summary>
    void SetPlaying(bool playing);

    /// <summary>A temporary correction rate, or null for exactly 1x; never saved or shown.</summary>
    void SetCorrectionRate(double? rate);

    /// <summary>The player gets to <paramref name="sourceSeconds"/> without a new stream.</summary>
    bool CanReachInPlace(double sourceSeconds);
}

/// <summary>The room side of a Watch Party: <see cref="WatchPartySession"/>.</summary>
public interface IRoomLink
{
    WatchPartyRoom? Room { get; }

    bool IsConnected { get; }

    long ConnectionEpoch { get; }

    /// <summary>Server time minus local time in milliseconds; null before the pings measured it.</summary>
    double? ClockOffsetMs { get; }

    event EventHandler<WatchPartyDelivery>? CommandReceived;

    Task<bool> SendAsync(string frame);
}

public enum RoomTransportResult
{
    Sent,
    Denied,
    Reconnecting,
    NotInRoom,
}

/// <summary>
/// Binds one Watch Party to one player for the room's current playback, as Silo's Android app does
/// (<c>RoomPlaybackBinding.kt</c>): it attaches the playback session on every connection and waits for the server's echo;
/// reports what the player really does every 1.5 s and stays quiet while it stalls or seeks; runs each command at its
/// <c>execute_at</c> on the server's clock, projected to that moment (a command waits for the clock, and for the room's
/// snapshot of its playback epoch when it came first); acknowledges the room's waiting command until the
/// room marks this member ready; reports a stall after 2 s; acknowledges recovery while catching up; and keeps a playing
/// member on the room with a small temporary rate. The viewer's play, pause and seek become requests: the player
/// changes only when the room's command comes back.
/// </summary>
/// <remarks>Used on the UI thread; its timers post there.</remarks>
public sealed class WatchPartyPlaybackBinding : IDisposable
{
    public const string HostSeeksText = "Only the host can seek the room.";
    public const string HostControlsText = "Only the host can control playback.";
    public const string ReconnectingText = "Reconnecting to room. Controls are temporarily unavailable.";

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ReadyRetry = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BufferingGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AttachEchoTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandQuiet = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BusyDefer = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EarlySlack = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan HoldMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportHoldMax = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingSeekTimeout = TimeSpan.FromSeconds(5);
    private const double GuestSeekTolerance = 5;
    private const double HostSeekTolerance = 15;

    private readonly IRoomLink _room;
    private readonly IRoomPlayer _player;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private ITimer? _ticker;
    private ITimer? _execution;
    private bool _disposed;

    private long _lastRevision = -1;
    private (long Epoch, string Session)? _attached;
    private DateTimeOffset _lastAttach = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;
    private (string Id, long Epoch)? _lastSeen;
    private Pending? _pending;
    private long _nextToken;
    private DateTimeOffset _appliedAt = DateTimeOffset.MinValue;
    private RoomReference? _reference;
    private DateTimeOffset _lastLoop = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBusy = DateTimeOffset.MinValue;
    private Readiness? _readiness;
    private DateTimeOffset _lastCatchUpAck = DateTimeOffset.MinValue;
    private DateTimeOffset? _stallStarted;
    private bool _stallReported;
    private double? _rate;
    private (string CommandId, bool Playing, DateTimeOffset Since)? _reportHold;
    private string? _reportedCommandId;
    private DateTimeOffset _pendingSeekAt;

    public WatchPartyPlaybackBinding(IRoomLink room, IRoomPlayer player, IUiDispatcher dispatcher, TimeProvider time, ILogger logger)
    {
        _room = room;
        _player = player;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>Something to tell the viewer: a refused control, or that the room is reconnecting.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>The target of this member's seek request until the room's seek to it runs; the timeline holds it.</summary>
    public double? PendingSeekSeconds { get; private set; }

    /// <summary>This viewer catches up alone while the room goes on.</summary>
    public bool IsCatchingUp { get; private set; }

    public void Start()
    {
        _room.CommandReceived += OnCommandReceived;
        _ticker = _time.CreateTimer(_ => _dispatcher.Post(Step), null, Tick, Tick);
    }

    /// <summary>The viewer's play or pause, asked of the room.</summary>
    public RoomTransportResult RequestPlayPause(bool pause)
    {
        if (_room.Room is not { IsPlaying: true } room)
        {
            return RoomTransportResult.NotInRoom;
        }
        if (!room.IsHost && !room.SelfCanControlTransport)
        {
            Notice?.Invoke(this, HostControlsText);
            return RoomTransportResult.Denied;
        }
        return Request(pause ? WatchPartyCommand.Pause : WatchPartyCommand.Play, _player.Observe().PositionSeconds, pause);
    }

    /// <summary>The viewer's seek, asked of the room: only the host's goes.</summary>
    public RoomTransportResult RequestSeek(double sourceSeconds)
    {
        if (_room.Room is not { IsPlaying: true } room || !double.IsFinite(sourceSeconds) || sourceSeconds < 0)
        {
            return RoomTransportResult.NotInRoom;
        }
        if (!room.IsHost)
        {
            Notice?.Invoke(this, HostSeeksText);
            return RoomTransportResult.Denied;
        }
        // The timeline holds the target from the request on; a request that does not go lets it go again.
        var (previous, previousAt) = (PendingSeekSeconds, _pendingSeekAt);
        PendingSeekSeconds = sourceSeconds;
        _pendingSeekAt = _time.GetUtcNow();
        var result = Request(WatchPartyCommand.Seek, sourceSeconds, room.IsPaused);
        if (result != RoomTransportResult.Sent)
        {
            (PendingSeekSeconds, _pendingSeekAt) = (previous, previousAt);
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _room.CommandReceived -= OnCommandReceived;
        _ticker?.Dispose();
        _execution?.Dispose();
        SetRate(null);
    }

    private RoomTransportResult Request(string action, double position, bool isPaused)
    {
        if (!_room.IsConnected)
        {
            Notice?.Invoke(this, ReconnectingText);
            return RoomTransportResult.Reconnecting;
        }
        _ = SendRequestAsync(WatchPartyFrames.TransportRequest(action, position, isPaused), action == WatchPartyCommand.Seek ? position : null);
        return RoomTransportResult.Sent;
    }

    // A request the connection did not take after all (it went between the check and the send, or was being renewed)
    // never reached the room: the viewer hears so, and a seek's target stops holding the timeline.
    private async Task SendRequestAsync(string frame, double? seekTarget)
    {
        if (await _room.SendAsync(frame))
        {
            return;
        }
        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (seekTarget is not null && Nullable.Equals(PendingSeekSeconds, seekTarget))
            {
                PendingSeekSeconds = null;
            }
            Notice?.Invoke(this, ReconnectingText);
        });
    }

    private void OnCommandReceived(object? sender, WatchPartyDelivery delivery)
    {
        if (_disposed || delivery.Epoch != _room.ConnectionEpoch)
        {
            return;
        }
        FollowRevision();
        var command = delivery.Command;
        if (_lastSeen == (command.CommandId, delivery.Epoch))
        {
            return;
        }
        _lastSeen = (command.CommandId, delivery.Epoch);
        // A command of a playback epoch the room already left is no command.
        if (_room.Room is { } room && command.SelectionRevision < room.SelectionRevision)
        {
            return;
        }
        _readiness = null;
        // The player keeps what it does until the command runs: without the server's clock yet, it waits for one.
        var pending = new Pending(++_nextToken, command, delivery.Epoch, _time.GetUtcNow());
        _pending = pending;
        Schedule(pending);
    }

    // At execute_at on the server's clock, at most MaxLead from now (the run checks it is due); a tick while the clock is unknown.
    private void Schedule(Pending pending) =>
        Arm(pending.Token, ServerNow() is { } serverNow ? Clamp(pending.Command.ExecuteAt - serverNow, TimeSpan.Zero, MaxLead) : Tick);

    private void Arm(long token, TimeSpan delay)
    {
        _execution?.Dispose();
        _execution = _time.CreateTimer(_ => _dispatcher.Post(() => Execute(token)), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Execute(long token)
    {
        if (_disposed)
        {
            return;
        }
        // A snapshot of a new epoch that landed since the last tick starts the binding over first, or it would undo what
        // this command sets (the room's reference, the readiness it waits for) on the next tick.
        FollowRevision();
        if (_pending is not { } pending || pending.Token != token)
        {
            return;
        }
        var command = pending.Command;
        // Another connection's command (its re-attach brings the room's own), or one of an epoch the room already left.
        if (pending.Epoch != _room.ConnectionEpoch || _room.Room is not { } room || command.SelectionRevision < room.SelectionRevision)
        {
            _pending = null;
            return;
        }
        // The room's snapshot of the command's epoch is still on its way, or the server's clock is not known yet: it waits.
        if (command.SelectionRevision > room.SelectionRevision || !room.IsPlaying || ServerNow() is not { } serverNow)
        {
            if (_time.GetUtcNow() - pending.Received >= HoldMax)
            {
                _pending = null;
                _logger.LogInformation("A Watch Party command waited {Seconds} s for its room or the server's clock and is dropped",
                    HoldMax.TotalSeconds);
                return;
            }
            Arm(token, Tick);
            return;
        }
        // Not due yet (one wait is at most MaxLead, and the clock may have moved): it waits again.
        if (command.ExecuteAt - serverNow > EarlySlack)
        {
            Schedule(pending);
            return;
        }
        var now = _time.GetUtcNow();
        var obs = _player.Observe();
        // A command that lands while the media is replaced would act on media that goes; it waits a moment.
        if (obs.IsBusy)
        {
            pending.DeferredSince ??= now;
            if (now - pending.DeferredSince < BusyDefer)
            {
                Arm(token, Tick);
                return;
            }
        }
        if (command.SessionId is { } session && session != obs.SessionId)
        {
            _pending = null;
            return;
        }
        _pending = null;
        bool commandPlays = command.Action switch
        {
            WatchPartyCommand.Play => command.PlaybackState != WatchPartyPlaybackStates.Waiting,
            WatchPartyCommand.Pause => false,
            _ => command.PlaybackState == WatchPartyPlaybackStates.Playing,
        };
        var next = new RoomReference(command.PositionSeconds, command.ExecuteAt,
            command.Action != WatchPartyCommand.Pause && command.PlaybackState == WatchPartyPlaybackStates.Playing);
        // Late execution projects forward: the room went on meanwhile.
        double target = next.PositionAt(serverNow);
        bool memberPlaying = obs.IsAdvancing && !obs.IsBusy;
        var decision = RoomCatchup.DecideCommand(command.Action, target - obs.PositionSeconds, memberPlaying, commandPlays,
            command.Action != WatchPartyCommand.Seek && _player.CanReachInPlace(target), obs.IntendsToPlay, room.IsHost);
        if (decision is not RoomDecision.Nudge && !(decision is RoomDecision.None && memberPlaying && commandPlays))
        {
            SetRate(null);
        }
        if (!commandPlays)
        {
            _player.SetPlaying(false);
        }
        switch (decision)
        {
            case RoomDecision.Seek:
                _player.SeekTo(target);
                break;
            case RoomDecision.Nudge nudge:
                SetRate(nudge.Rate);
                break;
        }
        if (commandPlays)
        {
            _player.SetPlaying(true);
        }
        _appliedAt = now;
        _reference = next;
        // The room's seek to this member's target answers the request.
        if (PendingSeekSeconds is { } asked && command.Action == WatchPartyCommand.Seek && Math.Abs(command.PositionSeconds - asked) <= 1)
        {
            PendingSeekSeconds = null;
        }
        _reportHold = (command.CommandId, commandPlays, now);
        if (command.PlaybackState == WatchPartyPlaybackStates.Waiting)
        {
            _readiness = new Readiness(command.CommandId, command.Action == WatchPartyCommand.Seek ? target : null, room.Generation);
        }
    }

    private void Step()
    {
        if (_disposed)
        {
            return;
        }
        var now = _time.GetUtcNow();
        FollowRevision();
        if (_room.Room is not { IsPlaying: true } room)
        {
            // Nothing plays in the room: nothing to correct. A command that came before the room's snapshot waits for it.
            SetRate(null);
            return;
        }
        if (PendingSeekSeconds is not null && now - _pendingSeekAt >= PendingSeekTimeout)
        {
            PendingSeekSeconds = null;
        }
        var obs = _player.Observe();
        if (obs.IsBusy)
        {
            _lastBusy = now;
        }
        if (!Attached(room, obs, now) || obs.SessionId is not { } sessionId)
        {
            // Nothing to converge to while detached or reconnecting.
            SetRate(null);
            return;
        }
        bool playable = !obs.IsBusy && !obs.IsBuffering;
        bool executedLatest = _pending is null;
        bool stalled = obs.IsBuffering && obs.IntendsToPlay && !obs.IsBusy;
        bool paused = obs.IsEnded || !obs.IntendsToPlay;
        AcknowledgeReadiness(room, obs, sessionId, playable && executedLatest, paused, now);
        AcknowledgeCatchUp(room, sessionId, obs, playable && executedLatest, paused, now);
        ReportStall(room, sessionId, obs, stalled, now);
        DriftLoop(room, obs, stalled, now);
        ReportState(room, sessionId, obs, playable, stalled, paused, now);
    }

    // Attaches the playback session on every connection and every new session, and again when the server never echoed it.
    private bool Attached(WatchPartyRoom room, RoomPlayerObservation obs, DateTimeOffset now)
    {
        (long, string)? key = obs.SessionId is { } session && _room.IsConnected ? (_room.ConnectionEpoch, session) : null;
        bool echoed = key is not null && room.AttachedSessionId == obs.SessionId;
        bool due = _attached != key || (!echoed && now - _lastAttach >= AttachEchoTimeout);
        if (key is { } attach && due && now - _lastAttach >= ReadyRetry)
        {
            _lastAttach = now;
            _attached = attach;
            _ = _room.SendAsync(WatchPartyFrames.Attach(attach.Item2));
        }
        return key is not null && _attached == key && echoed;
    }

    // The room waits for this member's command: acknowledged, again every 500 ms, until the room marks it ready.
    private void AcknowledgeReadiness(WatchPartyRoom room, RoomPlayerObservation obs, string sessionId, bool ready, bool paused, DateTimeOffset now)
    {
        if (_readiness is not { } waiting)
        {
            return;
        }
        if (room.PlaybackState != WatchPartyPlaybackStates.Waiting || (room.Generation > waiting.ArmedGeneration && room.Self?.IsReady == true))
        {
            _readiness = null;
            return;
        }
        double tolerance = room.IsHost ? HostSeekTolerance : GuestSeekTolerance;
        bool there = waiting.SeekTarget is not { } target || obs.IsEnded || Math.Abs(obs.PositionSeconds - target) <= tolerance;
        if (ready && there && now - waiting.LastSent >= ReadyRetry)
        {
            waiting.LastSent = now;
            _ = _room.SendAsync(WatchPartyFrames.Ready(sessionId, waiting.CommandId, obs.PositionSeconds, paused));
            _ = _room.SendAsync(WatchPartyFrames.StateReport(sessionId, waiting.CommandId, obs.PositionSeconds, paused, isReady: true));
            _lastReport = now;
        }
    }

    // The room went on without this viewer: once it can play, it says so until the room counts it in again.
    private void AcknowledgeCatchUp(WatchPartyRoom room, string sessionId, RoomPlayerObservation obs, bool ready, bool paused, DateTimeOffset now)
    {
        IsCatchingUp = room.SelfCatchingUp;
        if (IsCatchingUp && _readiness is null && room.Self?.IsReady != true && ready && now - _lastCatchUpAck >= ReadyRetry)
        {
            _lastCatchUpAck = now;
            _ = _room.SendAsync(WatchPartyFrames.Ready(sessionId, _lastSeen?.Id, obs.PositionSeconds, paused));
        }
    }

    // A stall shorter than the grace stays local; a longer one is reported once.
    private void ReportStall(WatchPartyRoom room, string sessionId, RoomPlayerObservation obs, bool stalled, DateTimeOffset now)
    {
        if (stalled && room.PlaybackState == WatchPartyPlaybackStates.Playing)
        {
            _stallStarted ??= now;
            if (!_stallReported && now - _stallStarted >= BufferingGrace)
            {
                _stallReported = true;
                _ = _room.SendAsync(WatchPartyFrames.Buffering(sessionId, obs.PositionSeconds, isPaused: false));
            }
        }
        else if (!stalled)
        {
            _stallStarted = null;
            _stallReported = false;
        }
    }

    // While the room and this member play, a small rate keeps it on the room; anything else runs at exactly 1x.
    private void DriftLoop(WatchPartyRoom room, RoomPlayerObservation obs, bool stalled, DateTimeOffset now)
    {
        if (_pending is not null)
        {
            return;
        }
        bool holds = room.PlaybackState == WatchPartyPlaybackStates.Playing && obs.IsAdvancing && !obs.IsBusy && !stalled;
        if (_reference is not { Playing: true } reference || ServerNow() is not { } serverNow || !holds)
        {
            SetRate(null);
            return;
        }
        var settledSince = _appliedAt > _lastBusy ? _appliedAt : _lastBusy;
        if (now - settledSince < RoomCatchup.LoopSettle || now - _lastLoop < RoomCatchup.LoopInterval)
        {
            return;
        }
        _lastLoop = now;
        var decision = RoomCatchup.LoopDecision(reference.PositionAt(serverNow) - obs.PositionSeconds, _rate is not null);
        SetRate(decision is RoomDecision.Nudge nudge ? nudge.Rate : null);
    }

    // Only positions that are decisions: none while stalled or seeking, nor around a command about to run, nor before
    // the player shows the command it ran.
    private void ReportState(WatchPartyRoom room, string sessionId, RoomPlayerObservation obs, bool playable, bool stalled, bool paused, DateTimeOffset now)
    {
        if (_reportHold is { } hold)
        {
            if (obs.IntendsToPlay == hold.Playing)
            {
                _reportedCommandId = hold.CommandId;
                _reportHold = null;
            }
            else if (now - hold.Since >= ReportHoldMax)
            {
                _reportHold = null;
            }
        }
        bool readinessPending = _readiness is not null || (IsCatchingUp && room.Self?.IsReady != true);
        bool quiet = _pending is { } pending && ServerNow() is { } serverNow
            && (pending.Command.ExecuteAt - serverNow).Duration() <= CommandQuiet;
        if (stalled || obs.IsBusy || (readinessPending && !playable) || quiet || _reportHold is not null || now - _lastReport < ReportInterval)
        {
            return;
        }
        _lastReport = now;
        _ = _room.SendAsync(WatchPartyFrames.StateReport(sessionId, _reportedCommandId, obs.PositionSeconds, paused));
    }

    // A new playback epoch (a start, a restart with other content) starts the binding over, and only that: a command of
    // the new epoch that came before the room's snapshot stays, and runs now.
    private void FollowRevision()
    {
        if (_room.Room is not { } room || room.SelectionRevision == _lastRevision)
        {
            return;
        }
        _lastRevision = room.SelectionRevision;
        var held = _pending is { } pending && pending.Command.SelectionRevision >= room.SelectionRevision ? pending : null;
        ResetEpoch();
        if (held is not null)
        {
            _pending = held;
            Schedule(held);
        }
    }

    private void ResetEpoch()
    {
        _attached = null;
        _pending = null;
        _execution?.Dispose();
        _execution = null;
        _reference = null;
        _readiness = null;
        _reportHold = null;
        _reportedCommandId = null;
        _stallStarted = null;
        _stallReported = false;
        IsCatchingUp = false;
        PendingSeekSeconds = null;
        SetRate(null);
    }

    private void SetRate(double? rate)
    {
        if (rate is { } value && Math.Abs(value - 1) < 0.001)
        {
            rate = null;
        }
        if (Nullable.Equals(rate, _rate))
        {
            return;
        }
        _rate = rate;
        _logger.LogDebug("Watch Party correction rate {Rate}", rate ?? 1);
        _player.SetCorrectionRate(rate);
    }

    private DateTimeOffset? ServerNow() => _room.ClockOffsetMs is { } offset ? _time.GetUtcNow().AddMilliseconds(offset) : null;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private sealed class Pending(long token, WatchPartyCommand command, long epoch, DateTimeOffset received)
    {
        public long Token { get; } = token;

        public DateTimeOffset Received { get; } = received;

        public WatchPartyCommand Command { get; } = command;

        public long Epoch { get; } = epoch;

        public DateTimeOffset? DeferredSince { get; set; }
    }

    private sealed class Readiness(string commandId, double? seekTarget, long armedGeneration)
    {
        public string CommandId { get; } = commandId;

        public double? SeekTarget { get; } = seekTarget;

        public long ArmedGeneration { get; } = armedGeneration;

        public DateTimeOffset LastSent { get; set; } = DateTimeOffset.MinValue;
    }
}
