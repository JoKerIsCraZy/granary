namespace Granary.Core.ViewModels.Cards;

/// <summary>How a card is drawn: a 2:3 poster, or a 16:9 still/backdrop (Continue Watching, episodes).</summary>
public enum MediaCardShape
{
    Poster,
    Landscape,
}

/// <summary>
/// Everything a card shows, already worded for display (docs/ui-design.md): the card controls only lay
/// it out. Texts that Silo shows upper-cased (overline, badges) arrive upper-cased.
/// </summary>
/// <param name="Id">The catalog content id the card opens.</param>
/// <param name="Type">The catalog item type (movie, series, season, episode, …).</param>
public sealed record MediaCard(string Id, string Type, string Title)
{
    /// <summary>A place in a paged grid whose title has not arrived yet; the grid draws it as a skeleton card.</summary>
    public static MediaCard Placeholder { get; } = new("", "", "") { IsPlaceholder = true };

    /// <summary>Whether this is <see cref="Placeholder"/>: nothing to open or show.</summary>
    public bool IsPlaceholder { get; private init; }

    public MediaCardShape Shape { get; init; } = MediaCardShape.Poster;

    /// <summary>Poster cards: "2021 SERIES", an episode's "S04E01". Landscape cards: "Season 1 Episode 5 • Episode title".</summary>
    public string? Subtitle { get; init; }

    /// <summary>An episode's poster card: the episode's own title under the series', "Safe Harbor", as on the web.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>A third line: "18 min left", "Next episode" on landscape cards; a watchlist title's status on its poster card.</summary>
    public string? Caption { get; init; }

    /// <summary>The caption warns, in amber: a watchlist title TMDB no longer lists as saved.</summary>
    public bool IsCaptionAttention { get; init; }

    /// <summary>
    /// A pill in the accent after the caption: "NEW SEASON" on a Continue Watching or Next Up card for the first episode of a
    /// season that has just begun, as the web's <c>ContinueWatchingCard</c>. Null on every other card.
    /// </summary>
    public string? CaptionBadge { get; init; }

    public Uri? ImageUrl { get; init; }

    /// <summary>The ThumbHash of <see cref="ImageUrl"/>'s picture: shown blurred at once, until the picture itself is.</summary>
    public string? ImageThumbhash { get; init; }

    /// <summary>
    /// The picture is the still of an episode the profile has not started, and the profile hides those
    /// (<see cref="Settings.EpisodeSpoilers"/>): it is drawn blurred, as the web's <c>scale-110 blur-xl</c>.
    /// </summary>
    public bool BlursImage { get; init; }

    /// <summary>
    /// What the card's badges can say (resolution, audio, ratings, show status, …); which of them it shows, where and how,
    /// is the profile's choice (<see cref="Overlays.CardOverlaySettings"/>). Null for a card without badges.
    /// </summary>
    public Overlays.OverlayData? Overlay { get; init; }

    /// <summary>A badge of the card's own, outside the profile's set: "NEW", a request's "APPROVED", a calendar's "PREMIERE", "SHARED".</summary>
    public string? StatusBadge { get; init; }

    /// <summary>Watched share between 0 and 1, or null without progress.</summary>
    public double? Progress { get; init; }

    /// <summary>The viewer watched it; a series or season all of it.</summary>
    public bool IsWatched { get; init; }

    /// <summary>Whether the artwork carries a watched mark: Silo's web marks season and episode cards only.</summary>
    public bool ShowsWatchedMark { get; init; }

    public bool IsFavorite { get; init; }

    public bool InWatchlist { get; init; }

    /// <summary>The viewer marked it Not interested: recommendations leave it out.</summary>
    public bool IsNotInterested { get; init; }

    /// <summary>What Play starts for this card (the next episode of a series), when the server says.</summary>
    public string? PlayContentId { get; init; }

    /// <summary>
    /// A title from TMDB the viewer may request: what the card's Request sends, as the web's <c>requestInputFromMediaResult</c>.
    /// Null for one that cannot be requested and for library titles.
    /// </summary>
    public Api.Requests.MediaRequestCreate? RequestOffer { get; init; }

    /// <summary>
    /// How the card marks the title its page is about, among its siblings: an episode's own card in its season's More
    /// Episodes, and a movie's own in its box set's row. <see cref="CurrentMark.None"/> on every other card.
    /// </summary>
    public CurrentMark CurrentMark { get; init; }

    /// <summary>The card is its page's own title (<see cref="CurrentMark"/>): its row opens with it in the middle.</summary>
    public bool IsCurrent => CurrentMark != CurrentMark.None;

    /// <summary>
    /// A box set's card (Search's Box sets, <see cref="MediaCardFactory.BoxSet"/>): its films, "3 films", in the corner beside
    /// the BOX SET pill, as the web's <c>BoxSetCard</c>. Null for any other card.
    /// </summary>
    public string? BoxSetFilms { get; init; }

    /// <summary>The name screen readers announce for the whole card.</summary>
    public string AccessibleName => string.Join(", ",
        new[] { Title, BoxSetFilms is { } films ? $"box set of {films}" : null, EpisodeTitle, Subtitle, CaptionBadge?.ToLowerInvariant(), CurrentName }
            .Where(text => !string.IsNullOrEmpty(text)));

    private string? CurrentName => CurrentMark switch
    {
        CurrentMark.NowViewing => "now viewing",
        CurrentMark.ThisFilm => "this film",
        _ => null,
    };
}

/// <summary>How a card marks the title its page is about (<see cref="MediaCard.IsCurrent"/>).</summary>
public enum CurrentMark
{
    /// <summary>Not the page's own title: no mark.</summary>
    None,

    /// <summary>An episode among its season's: an accent ring and "Now Viewing" top left, as the web's <c>EpisodeCarousel</c>.</summary>
    NowViewing,

    /// <summary>
    /// A movie among its box set's films: a light ring and "This film" at the poster's foot, as the web's <c>MovieBoxSetRow</c>.
    /// Its card offers no actions, as the apps' rows leave its menu out: the page's own buttons hold its state.
    /// </summary>
    ThisFilm,
}
