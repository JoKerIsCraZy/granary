using Granary.Core.Api.Catalog;

namespace Granary.Core.Settings;

/// <summary>
/// The profile's spoiler protection for episodes it has not started (<see cref="EpisodeSpoilerSettings"/>), and the rule every
/// place that shows an episode applies, as the web's <c>lib/episodeSpoilers.ts</c> and the apps' <c>EpisodeSpoilers</c>. Only
/// an episode's own still and description are hidden: its title stays, and movies, series and seasons never are.
/// </summary>
/// <param name="HidesImages">Blur the still of an episode the profile has not started.</param>
/// <param name="HidesOverviews">Leave out the description of an episode the profile has not started.</param>
public readonly record struct EpisodeSpoilers(bool HidesImages, bool HidesOverviews)
{
    /// <summary>Nothing hidden: the default, and what an older server or an unread profile gets.</summary>
    public static EpisodeSpoilers Off => default;

    /// <summary>
    /// An episode is unwatched while it is neither played nor in progress and has no saved position.
    /// </summary>
    public static bool IsUnwatched(bool played, bool? isInProgress, double? positionSeconds) =>
        !played && isInProgress != true && !(positionSeconds > 0);

    /// <summary>No watch state counts as unwatched, so an episode is never shown just because its payload left it out.</summary>
    public static bool IsUnwatched(WatchRollup? watch) => watch is null || IsUnwatched(watch.Played, watch.IsInProgress, watch.PositionSeconds);

    /// <summary>An image is the episode's own still unless the server says it is other artwork; older servers do not say.</summary>
    public static bool IsEpisodeStill(bool? provenance) => provenance != false;

    public bool HidesImage(bool isUnwatched, bool? isEpisodeStill) => HidesImages && isUnwatched && IsEpisodeStill(isEpisodeStill);

    public bool HidesOverview(bool isUnwatched) => HidesOverviews && isUnwatched;

    public bool HidesImage(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return HidesImage(IsUnwatched(episode.UserData), episode.StillIsEpisodeStill);
    }

    public bool HidesOverview(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return HidesOverview(IsUnwatched(episode.UserData));
    }

    /// <summary>A title of a row, by the played mark and the resume position it carries, as the web's <c>HeroBanner</c>.</summary>
    public bool HidesOverview(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type == CatalogItemTypes.Episode
            && HidesOverview(IsUnwatched(item.UserState?.Played == true, null, item.PositionSeconds));
    }

    /// <summary>
    /// A Continue Watching or Next Up card, by the picture it shows (<paramref name="showsBackdrop"/>, else its poster): rows
    /// carry the played mark and the resume position, as the web's <c>ContinueWatchingCard</c> reads them.
    /// </summary>
    public bool HidesImage(CatalogItemBase item, bool showsBackdrop)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type == CatalogItemTypes.Episode
            && HidesImage(IsUnwatched(item.UserState?.Played == true, null, item.PositionSeconds),
                showsBackdrop ? item.BackdropIsEpisodeStill : item.PosterIsEpisodeStill);
    }
}
