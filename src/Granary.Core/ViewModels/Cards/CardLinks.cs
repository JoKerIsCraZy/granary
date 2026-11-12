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
/// The lines under a card as links of their own for the pointer, as the web's cards (<c>hover:underline</c> in
/// <c>MediaCardArtwork.tsx</c>, <c>ItemCard.tsx</c>, <c>SectionItemCard.tsx</c>, <c>ContinueWatchingCard.tsx</c>): the line
/// under the pointer is underlined, and a click or tap on it opens what it stands for. The title line of an episode's card
/// opens its series (<see cref="MediaCard.HeadingId"/>); every other line opens the card itself, as its artwork does. The
/// keyboard, a remote and screen readers know the card as one button and never meet these.
/// </summary>
public static class CardLinks
{
    /// <summary>A title's name and the line under it: a box set's card, a TMDB title's.</summary>
    public const CardLines TitleAndSubtitle = CardLines.Title | CardLines.Subtitle;

    /// <summary>A library title's poster: its name, an episode's own title, and the year or the episode's code.</summary>
    public const CardLines Poster = CardLines.Title | CardLines.EpisodeTitle | CardLines.Subtitle;

    /// <summary>A Continue Watching or Next Up card: its name, the episode's line and the time left.</summary>
    public const CardLines Landscape = CardLines.Title | CardLines.Subtitle | CardLines.Caption;

    private const CardLines EpisodeLines = CardLines.EpisodeTitle | CardLines.Subtitle;

    /// <summary>
    /// The lines to underline while the pointer is over <paramref name="line"/>: that line when it is a link, together with
    /// the other line of the same link (<see cref="MediaCard.JoinsEpisodeLines"/>). None for plain text, for a card that
    /// opens nothing (a placeholder, a film's own card in its box set's row), and while the card's grid selects, where a
    /// click picks the card.
    /// </summary>
    public static CardLines Underlined(MediaCard? card, CardLines line, bool isSelecting = false)
    {
        if (card is null || isSelecting || card.IsPlaceholder || card.CurrentMark == CurrentMark.ThisFilm || (card.Links & line) == CardLines.None)
        {
            return CardLines.None;
        }
        return card.JoinsEpisodeLines && (line & EpisodeLines) != CardLines.None ? card.Links & EpisodeLines : line;
    }

    /// <summary>
    /// Whether a click or tap on <paramref name="line"/> opens the card's heading rather than the card: the title line of a
    /// card whose title stands for something else, an episode's series.
    /// </summary>
    public static bool OpensHeading(MediaCard? card, CardLines line, bool isSelecting = false) =>
        line == CardLines.Title && card is { HeadingId.Length: > 0 } && Underlined(card, line, isSelecting) != CardLines.None;
}
