namespace Granary.Core.Abstractions;

/// <summary>
/// A status other people see while Granary plays: Discord's "current activity". The player shows what plays there
/// (<see cref="ViewModels.Player.PlayerRichPresence"/>), on the UI thread; an implementation neither blocks nor throws.
/// </summary>
public interface IRichPresence
{
    /// <summary>Shows <paramref name="state"/> in place of what showed.</summary>
    void Show(RichPresenceState state);

    /// <summary>Takes the status away: nothing plays any more.</summary>
    void Clear();
}

/// <summary>What the status shows: its two lines, the timeline, the poster and the title's page.</summary>
/// <param name="Details">The first line: the series, the film, or the title the player shows; within Discord's limits.</param>
/// <param name="State">The second line, such as "S01E03 · Pilot" or the year; null for none.</param>
/// <param name="StartedAt">Where the timeline starts, so it reads the position; null without a timeline.</param>
/// <param name="EndsAt">Where the timeline ends, so it reads the time left; null without a timeline.</param>
/// <param name="Poster">The title's poster on TMDB; null for Granary's own picture. Never an address of the user's server.</param>
/// <param name="Link">The title's page on TMDB, for "View on TMDB"; null without one.</param>
public sealed record RichPresenceState(string Details, string? State, DateTimeOffset? StartedAt, DateTimeOffset? EndsAt, Uri? Poster, Uri? Link);
