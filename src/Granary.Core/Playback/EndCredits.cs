using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>
/// Where a file's end credits start, found as Silo Server finds them (<c>markers.CreditsLimits.EndCreditsStart</c>,
/// silo-server 47a85e4): the server marks a film or an episode watched once playback reaches them, whichever comes first of
/// them and its watched threshold. Silo's Apple app counts them so in its own checks (<c>PlayerMarkerTimeline.endCreditsStart</c>).
/// </summary>
public static class EndCredits
{
    // The tail window credits must start in: the last 450 s or 40 % of an episode, the last 900 s or 25 % of anything else
    // (the server's EpisodeCreditsTail* and MovieCreditsTail*). A credits marker earlier in the file never finishes it.
    private const double EpisodeTailSeconds = 450;
    private const double EpisodeTailFraction = 0.4;
    private const double MovieTailSeconds = 900;
    private const double MovieTailFraction = 0.25;

    /// <summary>
    /// The start of the last credits marker that starts in the tail window of a file of <paramref name="duration"/> seconds
    /// and before its end, or null when there is none. The last one, since a file that spans several episodes ends with the
    /// last one's.
    /// </summary>
    public static double? Start(IEnumerable<(string Kind, double StartSeconds)> markers, double duration, bool isEpisode)
    {
        ArgumentNullException.ThrowIfNull(markers);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return null;
        }
        double tail = isEpisode ? Math.Min(EpisodeTailSeconds, EpisodeTailFraction * duration) : Math.Min(MovieTailSeconds, MovieTailFraction * duration);
        double windowStart = duration - tail;
        double? start = null;
        foreach (var (kind, seconds) in markers)
        {
            if (kind == WatchMarkerKinds.Credits && double.IsFinite(seconds) && seconds >= windowStart && seconds < duration && !(seconds <= start))
            {
                start = seconds;
            }
        }
        return start;
    }
}
