using Granary.Core.Api.Catalog;

namespace Granary.Core.Playback;

/// <summary>The episode that plays, as far as finding the next one needs it.</summary>
public sealed record PlayingEpisode(string ContentId, string SeriesId, string SeriesTitle, int SeasonNumber, int EpisodeNumber);

/// <summary>The episode of a series that plays after the current one (docs/api-contract.md, 10.7).</summary>
public sealed record NextEpisode(string ContentId, int SeasonNumber, int EpisodeNumber, string Title)
{
    public string? Overview { get; init; }

    /// <summary><c>YYYY-MM-DD</c>.</summary>
    public string? AirDate { get; init; }

    public int RuntimeMinutes { get; init; }

    public Uri? StillUrl { get; init; }

    /// <summary>
    /// The smallest (season, episode) strictly after <paramref name="season"/>/<paramref name="episode"/> in
    /// <paramref name="pool"/>, as Android's NextEpisodeResolver picks it; null at the end.
    /// </summary>
    public static Episode? After(IEnumerable<Episode> pool, int season, int episode)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return pool.Where(e => e.SeasonNumber > season || (e.SeasonNumber == season && e.EpisodeNumber > episode))
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .FirstOrDefault();
    }
}
