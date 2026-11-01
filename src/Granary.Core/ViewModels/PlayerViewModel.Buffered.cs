using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// What the player holds of the stream, lighter on the seek bar, as Silo's web player draws a video's buffered ranges.
public sealed partial class PlayerViewModel
{
    // The engine's ranges, offset and length the shown ones were made from: a position update alone makes none anew.
    private (BufferedRanges From, double Offset, double Duration) _bufferedFrom = (BufferedRanges.None, 0, 0);

    /// <summary>
    /// The stretches of the title the player holds, in source seconds within <see cref="DurationSeconds"/>, as
    /// <see cref="PositionSeconds"/> is; none while the length is unknown and while the stream cannot be sought.
    /// </summary>
    [ObservableProperty]
    public partial BufferedRanges Buffered { get; private set; } = BufferedRanges.None;

    // Mapped as the position is (the same offset, at the same moment), so the played part and the ranges line up. A stream
    // on its way, being replaced or reconnecting shows none: what the engine holds then is not the stream that will play.
    private void FollowBuffered(PlaybackSnapshot state, double offset)
    {
        var ranges = CanSeek ? state.Buffered : BufferedRanges.None;
        double duration = DurationSeconds;
        var (from, fromOffset, fromDuration) = _bufferedFrom;
        if (ReferenceEquals(ranges, from) && offset == fromOffset && duration == fromDuration)
        {
            return;
        }
        _bufferedFrom = (ranges, offset, duration);
        Buffered = ranges.OnTimeline(offset, duration);
    }

    private void ResetBuffered()
    {
        _bufferedFrom = (BufferedRanges.None, 0, 0);
        Buffered = BufferedRanges.None;
    }
}
