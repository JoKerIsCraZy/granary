using System.Globalization;
using Granary.Core.Api.Subtitles;

namespace Granary.Core.ViewModels.Player;

/// <summary>How the player words a subtitle's timing and sync, in the web player's words (<c>player/utils/subtitleSync.ts</c>).</summary>
public static class SubtitleSyncText
{
    // The frame rates a scale may convert between; the server reports a conversion as its exact ratio.
    private static readonly double[] FrameRates = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60];
    private const double FrameRateTolerance = 1e-6;

    /// <summary>"+2.3 s" / "−0.4 s": one decimal, with a true minus sign.</summary>
    public static string Offset(long offsetMs)
    {
        double seconds = Math.Abs(offsetMs) / 1000.0;
        return (offsetMs < 0 ? "−" : "+") + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// A stretch as the frame-rate conversion it most likely is ("25→23.976 fps"), or as a plain speed ("×1.0427 speed");
    /// null for none. Original time t plays at t × scale, so a subtitle cut for rate <c>from</c> on video at <c>to</c> has
    /// scale from / to.
    /// </summary>
    public static string? Scale(double scale)
    {
        if (!double.IsFinite(scale) || scale == 1)
        {
            return null;
        }
        foreach (double from in FrameRates)
        {
            foreach (double to in FrameRates)
            {
                if (from != to && Math.Abs(from / to - scale) <= FrameRateTolerance)
                {
                    return $"{Number(from)}→{Number(to)} fps";
                }
            }
        }
        return $"×{Number(Math.Round(scale, 4))} speed";
    }

    /// <summary>"+2.3 s · 25→23.976 fps": a correction in a few characters.</summary>
    public static string Timing(SubtitleTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        string? scale = Scale(timing.Scale);
        string? offset = timing.OffsetMs != 0 || scale is null ? Offset(timing.OffsetMs) : null;
        return string.Join(" · ", new[] { offset, scale }.Where(part => part is not null));
    }

    /// <summary>A running job's progress as a whole percentage; null once it ended.</summary>
    public static int? Percent(SubtitleSyncJob? job) =>
        job is { IsInProgress: true } ? (int)Math.Round(Math.Clamp(job.Progress ?? 0, 0, 1) * 100, MidpointRounding.AwayFromZero) : null;

    /// <summary>What a running job does, in a few words; null once it ended.</summary>
    public static string? Phase(SubtitleSyncJob? job) => job is { IsInProgress: true }
        ? job.Phase switch
        {
            "matching" => "Matching lines to speech…",
            "analyzing" => "Listening to the audio…",
            _ => "Waiting to start…",
        }
        : null;

    /// <summary>Why a failed job failed, in plain words.</summary>
    public static string Failure(string? failure) => failure switch
    {
        "subtitle_changed" => "The subtitle changed while it was syncing. Try again.",
        "no_audio" => "This video has no audio Silo can read.",
        "unavailable" => "The server is busy. Try again in a few minutes.",
        _ => "Sync failed. Try again.",
    };

    /// <summary>
    /// A track's one line under its row in the subtitle menu ("Syncing… 40%", "Synced −3.0 s"), or null when there is
    /// nothing to say: never synced, never adjusted.
    /// </summary>
    public static string? Status(SubtitleSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var timing = state.Timing;
        switch (state.Sync?.Status)
        {
            case SubtitleSyncStatuses.Pending:
            case SubtitleSyncStatuses.Running:
                return Percent(state.Sync) is > 0 and var percent ? $"Syncing… {percent}%" : "Syncing…";
            case SubtitleSyncStatuses.NoMatch:
                return "Doesn't match this video";
            case SubtitleSyncStatuses.Failed:
                return "Sync failed";
            case SubtitleSyncStatuses.AlreadySynced when timing.IsIdentity:
                return "Already in sync";
            case SubtitleSyncStatuses.Synced when timing.IsIdentity:
                return "Original timing";
            case SubtitleSyncStatuses.Synced when timing.SameAs(state.Sync!.Result):
                return "Synced " + Timing(timing);
        }
        return timing.IsIdentity ? null : "Timing adjusted " + Timing(timing);
    }

    // As JavaScript writes a number: no trailing zeros, invariant digits.
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
