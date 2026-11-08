namespace Granary.Core.Playback;

/// <summary>
/// Rewind on resume (<c>player.resume_rewind_seconds</c>, client-local in the contract): a resume starts this far before
/// the saved point, so the viewer finds the scene again. Applied as Silo's Android app does (PlaybackStartPosition.kt,
/// <c>applyResumeRewind</c>): only to a resume, never to Start over, a Watch Party, a restart or another file.
/// </summary>
public static class ResumeRewind
{
    /// <summary>
    /// None: a resume lands on the saved point. Silo's contract has 7; Granary departs from it on purpose, the owner's
    /// choice, because viewers expect the exact point. Settings → Playback → This PC brings a lead-in back.
    /// </summary>
    public const int DefaultSeconds = 0;

    /// <summary>The largest the contract allows; 0 turns the rewind off.</summary>
    public const int MaxSeconds = 30;

    // Android's MinResumeForRewindSeconds: a point closer to the start is too near it to matter.
    private const double FromSeconds = 30;

    /// <summary>
    /// Where a resume at <paramref name="position"/> starts: <paramref name="seconds"/> earlier from 30 s in, never
    /// before 0. An earlier point, or a rewind of 0, resumes exactly.
    /// </summary>
    public static double Apply(double position, int seconds) =>
        position >= FromSeconds && seconds > 0 ? Math.Max(0, position - seconds) : position;
}
