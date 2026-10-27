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

    public Uri? ImageUrl { get; init; }

    /// <summary>The ThumbHash of <see cref="ImageUrl"/>'s picture: shown blurred at once, until the picture itself is.</summary>
    public string? ImageThumbhash { get; init; }

    /// <summary>"1080P", "4K DV".</summary>
    public string? ResolutionBadge { get; init; }

    /// <summary>"EAC3", "ATMOS".</summary>
    public string? AudioBadge { get; init; }

    /// <summary>"RETURNING", "ENDED", "NEW".</summary>
    public string? StatusBadge { get; init; }

    /// <summary>"IMDB 7.5".</summary>
    public string? RatingBadge { get; init; }

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

    /// <summary>The name screen readers announce for the whole card.</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, EpisodeTitle, Subtitle }.Where(text => !string.IsNullOrEmpty(text)));
}
