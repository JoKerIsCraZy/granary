namespace Granary.Core.ViewModels.Cards;

/// <summary>The lines under a card's artwork, by what they show (<see cref="MediaCard"/>).</summary>
[Flags]
public enum CardLines
{
    None = 0,

    /// <summary><see cref="MediaCard.Title"/>.</summary>
    Title = 1,

    /// <summary><see cref="MediaCard.EpisodeTitle"/>.</summary>
    EpisodeTitle = 2,

    /// <summary><see cref="MediaCard.Subtitle"/>.</summary>
    Subtitle = 4,

    /// <summary><see cref="MediaCard.Caption"/>, without the pill after it.</summary>
    Caption = 8,
}

/// <summary>
/// The lines under a card as links for the pointer, as the web's cards (<c>hover:underline</c> in
/// <c>MediaCardArtwork.tsx</c>, <c>ItemCard.tsx</c>, <c>SectionItemCard.tsx</c>, <c>ContinueWatchingCard.tsx</c>): a click or
/// tap on a line opens what it stands for. The title line of an episode's card opens its series
/// (<see cref="MediaCard.HeadingId"/>); every other line opens the card itself, as its artwork does. Unlike the web, where
/// each link is underlined alone, the lines that open the same page are underlined together, so a card shows what one click
/// reaches: all the lines of a movie's card, and on an episode's card the series' name alone and the lines under it
/// together. The keyboard, a remote and screen readers know the card as one button and never meet these.
/// </summary>
public static class CardLinks
{
    /// <summary>A title's name and the line under it: a box set's card, a TMDB title's.</summary>
    public const CardLines TitleAndSubtitle = CardLines.Title | CardLines.Subtitle;

    /// <summary>A library title's poster: its name, an episode's own title, and the year or the episode's code.</summary>
    public const CardLines Poster = CardLines.Title | CardLines.EpisodeTitle | CardLines.Subtitle;

    /// <summary>A Continue Watching or Next Up card: its name, the episode's line and the time left.</summary>
    public const CardLines Landscape = CardLines.Title | CardLines.Subtitle | CardLines.Caption;

    /// <summary>
    /// The lines to underline while the pointer is over <paramref name="line"/>: when it is a link, every link line the card
    /// shows that opens the same page. None for plain text, for a card that opens nothing (a placeholder, a film's own card
    /// in its box set's row), and while the card's grid selects, where a click picks the card.
    /// </summary>
    public static CardLines Underlined(MediaCard? card, CardLines line, bool isSelecting = false)
    {
        if (card is null || isSelecting || card.IsPlaceholder || card.CurrentMark == CurrentMark.ThisFilm)
        {
            return CardLines.None;
        }
        var links = card.Links & Shown(card);
        if ((links & line) == CardLines.None)
        {
            return CardLines.None;
        }
        if (string.IsNullOrEmpty(card.HeadingId))
        {
            return links;
        }
        // The title opens the series, the lines under it the card.
        return line == CardLines.Title ? CardLines.Title : links & ~CardLines.Title;
    }

    /// <summary>
    /// The lines to underline while the pointer is between <paramref name="above"/> and <paramref name="below"/>: both and
    /// the rest of their link when they open the same page, so the underline does not blink on the way from one to the
    /// other. With no line <paramref name="below"/>, as for <paramref name="above"/> alone.
    /// </summary>
    public static CardLines Underlined(MediaCard? card, CardLines above, CardLines below, bool isSelecting = false)
    {
        var lines = Underlined(card, above, isSelecting);
        return below == CardLines.None || (lines & below) != CardLines.None ? lines : CardLines.None;
    }

    /// <summary>
    /// Whether a click or tap on <paramref name="line"/> opens the card's heading rather than the card: the title line of a
    /// card whose title stands for something else, an episode's series.
    /// </summary>
    public static bool OpensHeading(MediaCard? card, CardLines line, bool isSelecting = false) =>
        line == CardLines.Title && card is { HeadingId.Length: > 0 } && Underlined(card, line, isSelecting) != CardLines.None;

    // The lines the card has a text for. The pill after a caption is not one.
    private static CardLines Shown(MediaCard card) =>
        CardLines.Title
        | (string.IsNullOrEmpty(card.EpisodeTitle) ? CardLines.None : CardLines.EpisodeTitle)
        | (string.IsNullOrEmpty(card.Subtitle) ? CardLines.None : CardLines.Subtitle)
        | (string.IsNullOrEmpty(card.Caption) ? CardLines.None : CardLines.Caption);
}

/// <summary>Where the pointer is on a card.</summary>
public enum CardPointer
{
    /// <summary>Not on the card.</summary>
    Away,

    /// <summary>On the artwork, or on what lies over it.</summary>
    Artwork,

    /// <summary>On the lines under the artwork, or between them.</summary>
    Caption,
}

/// <summary>
/// What a card shows under the pointer, by where the pointer is, as the web's cards: the artwork's look (its zoom, the play
/// disc, the card's buttons) belongs to the artwork's box (the web's <c>group/media</c>), a line's underline to the
/// caption. Never both: over the caption only the link shows, over the artwork no line is underlined.
/// </summary>
public static class CardHover
{
    /// <summary>Whether the artwork shows its look under the pointer. Not while the card's grid selects.</summary>
    public static bool ShowsArtworkLook(MediaCard? card, CardPointer pointer, bool isSelecting = false) =>
        card is not null && !isSelecting && pointer == CardPointer.Artwork;

    /// <summary>
    /// The lines to underline (<see cref="CardLinks.Underlined(MediaCard?, CardLines, CardLines, bool)"/>): for the line the
    /// pointer is on, or the two it is between, and only while it is on the caption.
    /// </summary>
    public static CardLines Underlined(MediaCard? card, CardPointer pointer, CardLines line, CardLines below = CardLines.None,
        bool isSelecting = false) =>
        pointer == CardPointer.Caption ? CardLinks.Underlined(card, line, below, isSelecting) : CardLines.None;
}
