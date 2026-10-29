using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The subtitle menu's TIMING section for the subtitle that shows, as the web player's <c>TimingControls</c>: "Sync to
/// audio" and "Reset timing", a running sync's progress, the last result, and why there is nothing to do.
/// </summary>
public sealed record SubtitleTimingPanel
{
    public static SubtitleTimingPanel Hidden { get; } = new();

    /// <summary>The section shows: sync is available and the subtitle has something to offer or say.</summary>
    public bool IsVisible { get; init; }

    /// <summary>The buttons show: the server lets this viewer retime it.</summary>
    public bool ShowsActions { get; init; }

    public bool ShowsSync { get; init; }

    /// <summary>"Sync to audio", or "Syncing…" while it runs.</summary>
    public string SyncLabel { get; init; } = "Sync to audio";

    /// <summary>Its timing was changed: Reset puts the original back.</summary>
    public bool ShowsReset { get; init; }

    /// <summary>No request on its way and no job running.</summary>
    public bool AreActionsEnabled { get; init; }

    /// <summary>A job runs: the bar, its phase and percentage show.</summary>
    public bool IsRunning { get; init; }

    public int Percent { get; init; }

    public string PercentText => $"{Percent}%";

    public string PhaseText { get; init; } = "";

    /// <summary>How the last job ended; empty without one to tell.</summary>
    public string ResultText { get; init; } = "";

    public bool IsResultWarning { get; init; }

    /// <summary>What "Sync to audio" does, before the first result.</summary>
    public string HintText { get; init; } = "";

    /// <summary>The server refused this viewer a retiming.</summary>
    public bool IsForbidden { get; init; }

    public string ErrorText { get; init; } = "";

    /// <summary><see cref="Percent"/> for a progress bar.</summary>
    public double PercentValue => Percent;

    public bool HasResult => ResultText.Length > 0;

    /// <summary>A result in the section's grey; a warning shows in amber instead.</summary>
    public bool ShowsResult => HasResult && !IsResultWarning;

    public bool ShowsWarning => HasResult && IsResultWarning;

    public bool HasHint => HintText.Length > 0;

    public bool HasError => ErrorText.Length > 0;

    /// <summary>The section for <paramref name="entry"/>; <paramref name="canSync"/> says whether "Sync to audio" applies to it.</summary>
    public static SubtitleTimingPanel For(SubtitleSyncEntry? entry, bool isAvailable, bool canSync)
    {
        if (entry is null || !isAvailable)
        {
            return Hidden;
        }
        var job = entry.Job;
        bool running = entry.IsInProgress;
        bool canReset = !entry.State.Timing.IsIdentity;
        if (!entry.IsForbidden && !canSync && !canReset && entry.Error is null)
        {
            return Hidden;
        }
        var (result, warning) = running || entry.Error is not null ? ("", false) : Result(entry.State);
        return new SubtitleTimingPanel
        {
            IsVisible = true,
            ShowsActions = !entry.IsForbidden,
            ShowsSync = canSync,
            SyncLabel = running ? "Syncing…" : "Sync to audio",
            ShowsReset = canReset,
            AreActionsEnabled = !entry.IsBusy && !running,
            IsRunning = running,
            Percent = SubtitleSyncText.Percent(job) ?? 0,
            PhaseText = SubtitleSyncText.Phase(job) ?? "",
            ResultText = result,
            IsResultWarning = warning,
            HintText = !entry.IsForbidden && canSync && !running && result.Length == 0
                ? entry.State.IsExternal
                    ? "Matches the timing to the audio for everyone. The file itself isn't changed."
                    : "Matches the timing to the audio for everyone watching."
                : "",
            IsForbidden = entry.IsForbidden,
            ErrorText = entry.Error ?? "",
        };
    }

    private static (string Text, bool Warning) Result(SubtitleSyncState state) => state.Sync?.Status switch
    {
        SubtitleSyncStatuses.Synced when !state.Timing.IsIdentity => ("Synced to the audio: " + SubtitleSyncText.Timing(state.Timing), false),
        SubtitleSyncStatuses.AlreadySynced => ("Already matches the audio.", false),
        SubtitleSyncStatuses.NoMatch => ("Doesn't match this video's audio; probably for another release.", true),
        SubtitleSyncStatuses.Failed => (SubtitleSyncText.Failure(state.Sync.Failure), true),
        _ => ("", false),
    };
}
