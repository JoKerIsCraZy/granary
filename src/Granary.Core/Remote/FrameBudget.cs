namespace Granary.Core.Remote;

/// <summary>
/// How much one phone may send, counted as each frame arrives and before it is opened: <see cref="FramesPerSecond"/>
/// frames a second with bursts of <see cref="FrameBurst"/>, and <see cref="BytesPerSecond"/> bytes a second with bursts
/// of <see cref="ByteBurst"/>. That is far more than any phone sends (a seek dragged across the timeline sends a few
/// dozen commands a second, the heartbeat one ping every 3 seconds), and every frame costs a tag check, a decryption and
/// a parse before Granary knows what it is, the ones it skips and the heartbeat included. A phone over either is
/// dropped. Granary's own: Silo's receivers have no such limit. Used by one connection's read loop only.
/// </summary>
public sealed class FrameBudget(TimeProvider time)
{
    public const int FramesPerSecond = 200;
    public const int FrameBurst = 400;
    public const int BytesPerSecond = 256 * 1024;
    public const int ByteBurst = 1024 * 1024;

    private double _frames = FrameBurst;
    private double _bytes = ByteBurst;
    private long? _takenAt;

    /// <summary>Spends one frame of <paramref name="bytes"/>; false when either budget is used up.</summary>
    public bool TryTake(int bytes)
    {
        long now = time.GetTimestamp();
        if (_takenAt is { } before)
        {
            double seconds = time.GetElapsedTime(before, now).TotalSeconds;
            _frames = Math.Min(FrameBurst, _frames + (seconds * FramesPerSecond));
            _bytes = Math.Min(ByteBurst, _bytes + (seconds * BytesPerSecond));
        }
        _takenAt = now;
        if (_frames < 1 || _bytes < bytes)
        {
            return false;
        }
        _frames--;
        _bytes -= bytes;
        return true;
    }
}
