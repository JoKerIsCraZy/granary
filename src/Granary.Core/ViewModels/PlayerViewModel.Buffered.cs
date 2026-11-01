using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// What the player holds of the stream, lighter on the seek bar, as Silo's web player draws a video's buffered ranges.
public sealed partial class PlayerViewModel
{
    // The engine's ranges, offset and length the shown ones were made from: a position update alone makes none anew.
    private (BufferedRanges From, double Offset, double Duration) _bufferedFrom = (BufferedRanges.None, 0, 0);

    // The engine load of the snapshot that shows, the offset it began with (null until its first snapshot), and whether it
    // is held back: the same load with another offset still plays the stream before a new plan. Snapshots come in the
    // engine's order (OnStateChanged drops a stale one), so a load seen once never comes back.
    private long _bufferedLoad;
    private double? _bufferedLoadOffset;
    private bool _bufferedLoadHeld;

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
        if (state.LoadId != _bufferedLoad)
        {
            (_bufferedLoad, _bufferedLoadOffset, _bufferedLoadHeld) = (state.LoadId, null, false);
        }
        // Taken from the load's first snapshot, not its first seekable one: a plan taken before the stream showed a frame
        // must tell the two apart as well.
        _bufferedLoadOffset ??= offset;
        if (_bufferedLoadOffset != offset)
        {
            // A new plan was taken (a re-anchored seek, another quality or track) and its stream is not loaded yet: the
            // engine still holds the stream before, which the new offset would put in the wrong place. None until the
            // engine reports the new load.
            _bufferedLoadHeld = true;
        }
        bool shows = CanSeek && !_bufferedLoadHeld;
        ShowBuffered(shows ? state.Buffered : BufferedRanges.None, offset);
    }

    private void ShowBuffered(BufferedRanges ranges, double offset)
    {
        double duration = DurationSeconds;
        var (from, fromOffset, fromDuration) = _bufferedFrom;
        if (ReferenceEquals(ranges, from) && offset == fromOffset && duration == fromDuration)
        {
            return;
        }
        _bufferedFrom = (ranges, offset, duration);
        Buffered = ranges.OnTimeline(offset, duration);
    }

    // The engine's loads go on counting across titles, so what is known of them stays.
    private void ResetBuffered()
    {
        _bufferedFrom = (BufferedRanges.None, 0, 0);
        Buffered = BufferedRanges.None;
    }
}
