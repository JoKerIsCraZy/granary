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

    /// <summary>
    /// The largest (season, episode) strictly before <paramref name="season"/>/<paramref name="episode"/> that has a
    /// file the server can read, as the web player steps back over the others; null at the start.
    /// </summary>
    public static Episode? Before(IEnumerable<Episode> pool, int season, int episode)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return pool.Where(e => (e.SeasonNumber < season || (e.SeasonNumber == season && e.EpisodeNumber < episode))
                && e.Files.Any(f => !f.Unreadable))
            .OrderByDescending(e => e.SeasonNumber).ThenByDescending(e => e.EpisodeNumber)
            .FirstOrDefault();
    }

    /// <summary>A catalog episode as the player shows it, its still resolved against <paramref name="server"/>.</summary>
    public static NextEpisode Of(Episode episode, Uri server)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return new NextEpisode(episode.ContentId, episode.SeasonNumber, episode.EpisodeNumber, episode.Title)
        {
            Overview = episode.Overview,
            AirDate = episode.AirDate,
            RuntimeMinutes = episode.Runtime,
            StillUrl = Api.Images.ArtworkUrl.Resolve(server, episode.StillUrl),
        };
    }
}
