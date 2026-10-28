using Granary.Core.Api.WatchParty;

namespace Granary.Core.WatchParty;

/// <summary>
/// The room position a member follows: the latest command it applied. <see cref="PositionSeconds"/> is the room's at
/// <see cref="ExecuteAt"/>; while <see cref="Playing"/> the room advances from there at 1x. Snapshots never replace it.
/// </summary>
public sealed record RoomReference(double PositionSeconds, DateTimeOffset ExecuteAt, bool Playing)
{
    public double PositionAt(DateTimeOffset serverNow)
    {
        double elapsed = Playing ? Math.Max(0, (serverNow - ExecuteAt).TotalSeconds) : 0;
        return Math.Max(0, PositionSeconds + elapsed);
    }
}

/// <summary>How a member applies a command, or closes drift while both play.</summary>
public abstract record RoomDecision
{
    /// <summary>Seek to the target now.</summary>
    public sealed record Seek : RoomDecision;

    /// <summary>Close the drift with a temporary playback rate; no seek.</summary>
    public sealed record Nudge(double Rate) : RoomDecision;

    /// <summary>Leave the position alone.</summary>
    public sealed record None : RoomDecision;
}

/// <summary>
/// How a Watch Party member keeps its playhead on the room, shared by Silo's clients (Android's <c>RoomCatchup.kt</c>,
/// the web's <c>roomSyncCatchup.ts</c>). Drift is the room's position minus the member's: positive means behind, and a
/// small drift is closed by a rate of 1 ± 1–4 %, never saved or shown, instead of a seek that could rebuild the stream.
/// </summary>
public static class RoomCatchup
{
    /// <summary>No correction starts below this drift.</summary>
    public const double DeadbandSeconds = 0.4;

    /// <summary>A running nudge ends within this drift.</summary>
    public const double SettleSeconds = 0.1;

    /// <summary>Only above this drift does a playing guest seek.</summary>
    public const double HardSeekSeconds = 5;

    /// <summary>The host seeks above this instead: the server takes a host 2 s off as a decision and moves everyone.</summary>
    public const double HostSeekSeconds = 2;

    /// <summary>Above this a member at rest seeks, when the target is reachable without a new stream.</summary>
    public const double PausedSeekSeconds = 1;

    /// <summary>The drift loop waits this long after a command or a seek.</summary>
    public static readonly TimeSpan LoopSettle = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(500);

    /// <summary><c>1 + sign(d) · clamp(|d| / 25, 0.01, 0.04)</c>, in steps of 0.005; exactly 1 for no drift.</summary>
    public static double RateFor(double driftSeconds)
    {
        if (!double.IsFinite(driftSeconds) || driftSeconds == 0)
        {
            return 1;
        }
        double delta = Math.Clamp(Math.Abs(driftSeconds) / 25, 0.01, 0.04);
        int steps = (int)Math.Round(delta * 200, MidpointRounding.AwayFromZero);
        return (200 + (driftSeconds > 0 ? steps : -steps)) / 200.0;
    }

    /// <summary>How to apply a command at the moment it runs.</summary>
    /// <param name="memberPlaying">The player is advancing.</param>
    /// <param name="commandPlays">The command leaves the room playing.</param>
    /// <param name="reachableInPlace">The player gets to the target without a new stream.</param>
    /// <param name="memberIntendsToPlay">The player wants to play but may not advance yet (buffering, loading).</param>
    public static RoomDecision DecideCommand(string action, double driftSeconds, bool memberPlaying, bool commandPlays, bool reachableInPlace,
        bool memberIntendsToPlay, bool host)
    {
        // A seek is a room barrier: everyone lands on the same frame.
        if (action == WatchPartyCommand.Seek)
        {
            return new RoomDecision.Seek();
        }
        double drift = Math.Abs(driftSeconds);
        double hardSeek = host ? HostSeekSeconds : HardSeekSeconds;
        bool inPlaceSeek = drift > PausedSeekSeconds && reachableInPlace;
        if (action == WatchPartyCommand.Play && commandPlays && memberPlaying)
        {
            return drift <= DeadbandSeconds ? new RoomDecision.None()
                : drift > hardSeek ? new RoomDecision.Seek()
                : new RoomDecision.Nudge(RateFor(driftSeconds));
        }
        if (action == WatchPartyCommand.Play && commandPlays && memberIntendsToPlay)
        {
            return drift > hardSeek || inPlaceSeek ? new RoomDecision.Seek() : new RoomDecision.None();
        }
        // A pause, or a play this member starts from rest: the loop closes what is left once playing.
        return drift > hardSeek || inPlaceSeek ? new RoomDecision.Seek() : new RoomDecision.None();
    }

    /// <summary>The drift loop's step: a nudge runs until within <see cref="SettleSeconds"/>; a new one starts beyond the deadband.</summary>
    public static RoomDecision LoopDecision(double driftSeconds, bool nudging)
    {
        double drift = Math.Abs(driftSeconds);
        if (drift <= (nudging ? SettleSeconds : DeadbandSeconds))
        {
            return new RoomDecision.None();
        }
        return new RoomDecision.Nudge(RateFor(driftSeconds));
    }
}
