using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI.Text;

namespace Granary.App.Controls;

// The lines under the artwork as links of their own for the pointer, as the web's cards (hover:underline in
// MediaCardArtwork.tsx, ItemCard.tsx, SectionItemCard.tsx, ContinueWatchingCard.tsx): the line under the mouse is underlined
// and shows the hand, and a click or tap on an episode card's title line opens its series. Which lines are links, which go
// underlined together and what the title opens is the card's (CardLinks); here is only where the pointer is. Every other
// line opens the card through the button around it, as before, so nothing here changes what the keyboard, a remote or a
// screen reader meet. Nothing is made per card: the line is found from the pointer's place among the text blocks already
// there, the cards share one hand, and a card listens for presses and taps only once a pointer has come to it while its
// title opens its series.
public sealed partial class MediaCardView
{
    // One hand and one arrow for all cards, made for the first link the pointer comes to.
    private static InputCursor? _hand;
    private static InputCursor? _arrow;

    private CardLines _underlined;
    private bool _showsHand;
    private bool _followsTaps;
    // The press now down began on a title line that opens its series: the tap after it opens it.
    private bool _headingPressed;

    // The pointer came to the card or moved over it: the link under it shows. A finger has no hover, as the web's hover
    // styles are for a pointer that can; its tap on a title line still opens the series, so the card listens for it from
    // here on. Any pointer enters a card before it presses on it, so the first press is heard too.
    private void ShowLink(PointerRoutedEventArgs e)
    {
        if (LinkedCard() is not { } card)
        {
            Underline(CardLines.None);
            return;
        }
        if (card.HeadingId is not null && !_followsTaps)
        {
            _followsTaps = true;
            PointerPressed += OnLinkPressed;
            Tapped += OnLinkTapped;
        }
        var line = e.Pointer.PointerDeviceType == PointerDeviceType.Touch ? CardLines.None : LineAt(card, e.GetCurrentPoint(CaptionPanel).Position);
        Underline(CardLinks.Underlined(card, line, Pick is not null));
    }

    // The card while it has lines that are links: not without a caption (the profile's "Nothing" under posters).
    private MediaCard? LinkedCard() => Card is { Links: not CardLines.None } card && _caption != CardCaption.Artwork ? card : null;

    // The line at a place in the caption; none over the artwork or between two lines. A line is a link over the caption's
    // whole width, as the web's block links, and the time left only as far as its text, as the web's w-fit.
    private CardLines LineAt(MediaCard card, Point at)
    {
        if (at.Y < 0 || at.X < 0 || at.X >= CaptionPanel.ActualWidth)
        {
            return CardLines.None;
        }
        if (Holds(TitleText, at.Y))
        {
            return CardLines.Title;
        }
        bool hasEpisodeTitle = EpisodeTitleText.Visibility == Visibility.Visible;
        bool hasSubtitle = SubtitleText.Visibility == Visibility.Visible;
        // One link holds both lines and the gap between them (the web's SectionItemCard).
        if (card.JoinsEpisodeLines && hasEpisodeTitle && hasSubtitle
            && at.Y >= EpisodeTitleText.ActualOffset.Y && at.Y < SubtitleText.ActualOffset.Y + SubtitleText.ActualHeight)
        {
            return CardLines.EpisodeTitle;
        }
        if (hasEpisodeTitle && Holds(EpisodeTitleText, at.Y))
        {
            return CardLines.EpisodeTitle;
        }
        if (hasSubtitle && Holds(SubtitleText, at.Y))
        {
            return CardLines.Subtitle;
        }
        // With "NEW SEASON" after it, the text is the link and the pill is not.
        bool badged = BadgedCaption is { Visibility: Visibility.Visible };
        FrameworkElement line = badged ? BadgedCaption! : CaptionText;
        var text = badged ? BadgedCaptionText : CaptionText;
        return line.Visibility == Visibility.Visible && Holds(line, at.Y) && at.X < line.ActualOffset.X + text.DesiredSize.Width
            ? CardLines.Caption
            : CardLines.None;
    }

    private static bool Holds(FrameworkElement line, double y) => y >= line.ActualOffset.Y && y < line.ActualOffset.Y + line.ActualHeight;

    // An underline changes no size: the text stays where it is. Only the lines that change are touched.
    private void Underline(CardLines lines)
    {
        var changed = lines ^ _underlined;
        if (changed != CardLines.None)
        {
            _underlined = lines;
            Decorate(TitleText, CardLines.Title);
            Decorate(EpisodeTitleText, CardLines.EpisodeTitle);
            Decorate(SubtitleText, CardLines.Subtitle);
            Decorate(CaptionText, CardLines.Caption);
            Decorate(BadgedCaptionText, CardLines.Caption);
        }
        ShowHand(lines != CardLines.None);

        void Decorate(TextBlock? text, CardLines line)
        {
            if (text is not null && (changed & line) != CardLines.None)
            {
                text.TextDecorations = (lines & line) != CardLines.None ? TextDecorations.Underline : TextDecorations.None;
            }
        }
    }

    // The web's links show the hand. Off a link the card takes the arrow, not null: null leaves the cursor shown as it is
    // until the pointer reaches another element (PlayerPage.ApplyCursor), and the hand would stay over the artwork.
    private void ShowHand(bool hand)
    {
        if (hand != _showsHand)
        {
            _showsHand = hand;
            ProtectedCursor = hand
                ? (_hand ??= InputSystemCursor.Create(InputSystemCursorShape.Hand))
                : (_arrow ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        }
    }

    // A press on a title line that opens its series is the card's own: the button around the card never sees it, so it
    // neither presses nor clicks, and the tap that follows opens the series. The card takes focus, as on any click on it.
    // Every other press goes on to the button: the right button's too, for the menu.
    private void OnLinkPressed(object sender, PointerRoutedEventArgs e)
    {
        _headingPressed = false;
        if (LinkedCard() is not { HeadingId: not null } card)
        {
            return;
        }
        var point = e.GetCurrentPoint(CaptionPanel);
        if (point.Properties.IsLeftButtonPressed && CardLinks.OpensHeading(card, LineAt(card, point.Position), Pick is not null))
        {
            _headingPressed = true;
            e.Handled = true;
            _host?.Focus(FocusState.Pointer);
        }
    }

    // A press that turned into a scroll or left the line ends in no tap, or in one somewhere else.
    private void OnLinkTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_headingPressed)
        {
            return;
        }
        _headingPressed = false;
        if (LinkedCard() is { } card && CardLinks.OpensHeading(card, LineAt(card, e.GetPosition(CaptionPanel)), Pick is not null))
        {
            e.Handled = true;
            _actions.OpenHeading(card);
        }
    }
}
