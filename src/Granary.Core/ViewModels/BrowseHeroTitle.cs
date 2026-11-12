using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Requests;
using Granary.Core.Artwork;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

/// <summary>
/// What the top of a page of rows (Home, For You) shows of a title (docs/ui-design.md, Home): its logo, or its name without
/// one, a line of details, the overview and the backdrop behind them. An episode is headed by its series.
/// </summary>
/// <param name="Key">The title's content id: the same title loaded again is the same hero.</param>
/// <param name="Meta">
/// "2008 · 2h 32m · IMDb 9.0 · Action · Crime · PG-13", an episode's "S1 E1 · Kein Zufall · 25m · TV-14"; empty when nothing
/// is known.
/// </param>
/// <param name="Overview">Null when there is none, or for an unwatched episode while the profile hides descriptions.</param>
public sealed record BrowseHeroTitle(string Key, string Heading, string Meta, string? Overview, Uri? LogoUrl, Uri? BackdropUrl, string? BackdropThumbhash)
{
    private const string Separator = " · ";

    /// <summary>
    /// The rating <see cref="Meta"/> names, which the hero draws as its source's mark and its score, as the web's
    /// <c>RatingEntry</c>; null when the line has none.
    /// </summary>
    public BrowseHeroRating? Rating { get; init; }

    /// <summary><see cref="Meta"/> up to its <see cref="Rating"/>, "2008 · 2h 32m · "; all of it without one.</summary>
    public string MetaBeforeRating => Rating is { } rating ? Meta[..rating.Start] : Meta;

    /// <summary><see cref="Meta"/> after its <see cref="Rating"/>, " · Action · Crime · PG-13"; empty without one.</summary>
    public string MetaAfterRating => Rating is { } rating ? Meta[(rating.Start + rating.Text.Length)..] : "";

    /// <summary>
    /// The backdrop is the still of an episode the profile has not started, while it hides those: it shows blurred, as the
    /// web's <c>HeroBanner</c> does (<see cref="EpisodeSpoilers"/>).
    /// </summary>
    public bool BlursBackdrop { get; init; }

    /// <summary>
    /// What a hero's pictures of the title depend on: the title, whether its backdrop is blurred, and which backdrop it is,
    /// without what only authorizes its address (<see cref="ArtworkCacheKey"/>). The same title loaded again has its
    /// addresses signed anew and keeps the pictures shown; one whose backdrop came in another size, as after its page
    /// asked for wider ones, or was replaced on the server, gets it shown.
    /// </summary>
    public (string Key, bool Blurs, string? Backdrop) Pictures =>
        (Key, BlursBackdrop, BackdropUrl is { IsAbsoluteUri: true } url ? ArtworkCacheKey.From(url) : null);

    /// <summary>The title of <paramref name="item"/>, hiding what <paramref name="spoilers"/> hide of an unwatched episode.</summary>
    public static BrowseHeroTitle From(CatalogItemBase item, Uri server, EpisodeSpoilers spoilers)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        string heading = isEpisode && !string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.SeriesTitle : item.Title;
        var (meta, rating) = DetailsOf(item);
        return new BrowseHeroTitle(
            item.ContentId,
            heading,
            meta,
            string.IsNullOrWhiteSpace(item.Overview) || spoilers.HidesOverview(item) ? null : item.Overview.Trim(),
            ArtworkUrl.Resolve(server, item.LogoUrl),
            ArtworkUrl.Resolve(server, item.BackdropUrl),
            item.BackdropThumbhash)
        {
            Rating = rating,
            BlursBackdrop = spoilers.HidesImage(item, showsBackdrop: true),
        };
    }

    /// <summary>
    /// A title from TMDB, as Requests' Discover lists it: its name for a logo (TMDB gives none here), "2026 · Movie" and
    /// where a request for it stands, in the words of its card's badge ("Approved", "Available", "Blocked"), and its backdrop.
    /// </summary>
    public static BrowseHeroTitle Of(RequestMediaResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Of(result, RequestCards.StateText(result.Request, result.Availability));
    }

    /// <summary>As <see cref="Of(RequestMediaResult)"/>, saying where its request stands now (<paramref name="state"/>, "Pending").</summary>
    public static BrowseHeroTitle Of(RequestMediaResult result, string? state)
    {
        ArgumentNullException.ThrowIfNull(result);
        string?[] parts =
        [
            result.Year is int year and > 0 ? year.ToString(CultureInfo.InvariantCulture) : null,
            result.MediaType == "series" ? "Series" : "Movie",
            state,
        ];
        return new BrowseHeroTitle(
            RequestCards.IdOf(result),
            result.Title,
            string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part))),
            string.IsNullOrWhiteSpace(result.Overview) ? null : result.Overview.Trim(),
            null,
            TmdbImages.Poster(result.BackdropPath, BackdropSize),
            null);
    }

    // TMDB's backdrop at the width the hero decodes it (BrowseHeroLayout.BackdropDecodeWidth, 1280 at most on a 1920 window).
    private const string BackdropSize = "w1280";

    // How many genres the line names, as the web's.
    private const int MetaGenres = 2;

    // In the order of the web's formatHeroMetadata (components/heroMetadata.ts). A movie its year, length, rating (IMDb's, or
    // TMDB's without one), two genres and certificate; a series the same without a length, which is an episode's; an episode
    // where it sits in its series, its own title, its length and certificate, and neither rating nor genres, as on the web.
    // The certificate is in capitals in every line, as the web's.
    private static (string Meta, BrowseHeroRating? Rating) DetailsOf(CatalogItemBase item)
    {
        string? certificate = item.ContentRating?.ToUpperInvariant();
        if (item.Type == CatalogItemTypes.Episode)
        {
            return (Joined([EpisodeCode(item.SeasonNumber, item.EpisodeNumber), item.Title, DetailText.Runtime(item.Runtime), certificate]), null);
        }
        string before = Joined([DetailText.Year(item.Year), item.Type == CatalogItemTypes.Movie ? DetailText.Runtime(item.Runtime) : null]);
        string after = Joined([.. Genres(item), certificate]);
        if (CardRating.Primary(item) is not { } rating)
        {
            return (Joined([before, after]), null);
        }
        int start = before.Length > 0 ? before.Length + Separator.Length : 0;
        return (Joined([before, rating.AccessibleName, after]), new BrowseHeroRating(rating.Name, rating.Display, start));
    }

    // The first genres, each named once.
    private static IEnumerable<string> Genres(CatalogItemBase item) =>
        item.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)).Select(genre => genre.Trim()).Distinct(StringComparer.Ordinal).Take(MetaGenres);

    private static string Joined(IEnumerable<string?> parts) =>
        string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));

    private static string? EpisodeCode(int? season, int? episode) =>
        season is int s && episode is int e ? string.Create(CultureInfo.InvariantCulture, $"S{s} E{e}") : null;
}

/// <summary>The rating in a hero's line of details: its source's mark and its score, "IMDb" and "8.4".</summary>
/// <param name="Start">Where <see cref="Text"/> begins in <see cref="BrowseHeroTitle.Meta"/>.</param>
public sealed record BrowseHeroRating(string Name, string Display, int Start)
{
    /// <summary>"IMDb 8.4", as the line reads.</summary>
    public string Text => $"{Name} {Display}";
}
