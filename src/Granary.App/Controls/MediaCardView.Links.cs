using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI.Text;

namespace Granary.App.Controls;

// What a card shows under the pointer, by where the pointer is (CardHover), as the web's cards: over the artwork its look
// (the zoom, the play disc, the card's buttons), over the lines under it only their links. The web's overlay belongs to the
// artwork's box (group/media in ItemCard.tsx, SectionItemCard.tsx, ContinueWatchingCard.tsx) and its links to the caption
// (hover:underline in MediaCardArtwork.tsx): the line under the mouse is underlined with the other lines that open the same
// page and shows the hand, and a click or tap on an episode card's title line opens its series. Which lines are links, which
// go underlined together and what the title opens is the card's (CardLinks); here is only where the pointer is.
// Every click stays the click of the button around the card, with its pressed look, its focus and its "release elsewhere
// cancels": the card only notes a press that begins on a title line that opens its series (HeadingPress), and the row or
// grid asks TryOpenHeading first when the button clicks (CardClick). A key's click never finds such a press, so nothing
// here changes what the keyboard, a remote or a screen reader meet.
// Nothing is made per card: the line is found from the pointer's place among the text blocks already there, the cards
// share their cursors, and only a card a pointer has come to while its title opens its series listens for presses.
public sealed partial class MediaCardView
{
    private enum LinkCursor
    {
        // The card has no cursor of its own.
        None,
        Hand,
        Arrow,
    }

    // One hand and one arrow for all cards, made for the first link the pointer comes to.
    private static InputCursor? _hand;
    private static InputCursor? _arrow;

    private CardLines _underlined;
    private LinkCursor _cursor;
    // The card let go of its cursor while the hand showed, and the pointer may still be on it (a card given another title
    // under a resting pointer): its next move off a link takes the arrow.
    private bool _handMayLinger;
    // Made with the handlers below, for a card a pointer came to while its title opens its series.
    private HeadingPress? _headingPress;
    private PointerEventHandler? _onPressed;
    private KeyEventHandler? _onHostKey;
    private PointerEventHandler? _onHostCaptureLost;
    // The button those two are on.
    private ButtonBase? _pressHost;

    /// <summary>
    /// A click on the card's button: when it ends a pointer's press on a title line that opens its series, opens the series
    /// and says so. The row or grid asks before it runs the card's own command or picks the card (<see cref="CardClick"/>).
    /// </summary>
    public bool TryOpenHeading() => _headingPress is { } press && press.TryOpen(Card, _actions, Pick is not null);

    /// <summary>
    /// Something that is no pointer and sends no key is about to click the card's button, a game controller's A: a press
    /// still noted is forgotten first, as before a key's click (<see cref="OnHostKey"/>), so it never opens the series.
    /// </summary>
    public void ForgetPress() => _headingPress?.Clear();

    // The pointer came to the card (entered) or moved over it. Over the artwork its look shows, and the buttons are brought
    // up to date first: whether the server keeps TMDB titles on the watchlist may have been read after the card showed.
    // Over the caption that look is off and the link under the pointer shows. The 11 px between the two are the button's,
    // not the card's: there neither shows, so the way from one to the other switches once and nothing blinks.
    // A finger has no hover, as the web's hover styles are for a pointer that can; its tap on a title line still opens the
    // series. Any pointer enters the caption before it presses there, so the card hears its first press too.
    private void ShowPointer(PointerRoutedEventArgs e, bool entered)
    {
        Point at = default;
        var pointer = CardPointer.Artwork;
        if (_caption != CardCaption.Artwork)
        {
            // By its place, not by the element the event names: that holds for a pointer coming in as for one moving.
            at = e.GetCurrentPoint(CaptionPanel).Position;
            pointer = at.Y >= 0 ? CardPointer.Caption : CardPointer.Artwork;
        }
        bool look = CardHover.ShowsArtworkLook(Card, pointer, Pick is not null);
        if (look && (entered || !_hovered))
        {
            EnsureActions();
            ShowState();
        }
        if (look != _hovered)
        {
            ShowHoverLook(look);
        }

        var lines = CardLines.None;
        if (pointer == CardPointer.Caption && LinkedCard() is { } card)
        {
            if (card.HeadingId is not null)
            {
                FollowPresses();
            }
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                var (line, below) = LineAt(at);
                lines = CardHover.Underlined(card, pointer, line, below, Pick is not null);
            }
        }
        Underline(lines);
        ShowHand(lines != CardLines.None);
    }

    // The pointer left the card, or the card left the screen or took another title: no underline stays, the cursor is the
    // page's again, and a press that got no click is forgotten.
    private void DropLinks()
    {
        Underline(CardLines.None);
        SetCursor(LinkCursor.None);
        _headingPress?.Clear();
    }

    // The card while it has lines that are links: not without a caption (the profile's "Nothing" under posters).
    private MediaCard? LinkedCard() => Card is { Links: not CardLines.None } card && _caption != CardCaption.Artwork ? card : null;

    // The line at a place in the caption, or the two lines the place lies between (CardLinks decides whether they are one
    // link). A line reaches over the caption's whole width, as the web's block links, and the time left only as far as its
    // text, as the web's w-fit; with "NEW SEASON" after it, the text is the line and the pill is not.
    private (CardLines Line, CardLines Below) LineAt(Point at)
    {
        (CardLines Line, CardLines Below) found = default;
        if (at.Y < 0 || at.X < 0 || at.X >= CaptionPanel.ActualWidth)
        {
            return found;
        }
        var above = CardLines.None;
        bool badged = BadgedCaption is { Visibility: Visibility.Visible };
        _ = Ends(TitleText, CardLines.Title) || Ends(EpisodeTitleText, CardLines.EpisodeTitle) || Ends(SubtitleText, CardLines.Subtitle)
            || Ends(badged ? BadgedCaption! : CaptionText, CardLines.Caption);
        return found;

        // Whether the place is on this line or above it, which ends the search down the lines.
        bool Ends(FrameworkElement element, CardLines line)
        {
            if (element.Visibility != Visibility.Visible)
            {
                return false;
            }
            double top = element.ActualOffset.Y;
            if (at.Y >= top + element.ActualHeight)
            {
                above = line;
                return false;
            }
            if (at.Y < top)
            {
                found = (above, line);
            }
            else if (line != CardLines.Caption || at.X < element.ActualOffset.X + (badged ? BadgedCaptionText : CaptionText).DesiredSize.Width)
            {
                found = (line, CardLines.None);
            }
            return true;
        }
    }

    // An underline changes no size: the text stays where it is. Only the lines that change are touched.
    private void Underline(CardLines lines)
    {
        var changed = lines ^ _underlined;
        if (changed == CardLines.None)
        {
            return;
        }
        _underlined = lines;
        Decorate(TitleText, CardLines.Title);
        Decorate(EpisodeTitleText, CardLines.EpisodeTitle);
        Decorate(SubtitleText, CardLines.Subtitle);
        Decorate(CaptionText, CardLines.Caption);
        Decorate(BadgedCaptionText, CardLines.Caption);

        void Decorate(TextBlock? text, CardLines line)
        {
            if (text is not null && (changed & line) != CardLines.None)
            {
                text.TextDecorations = (lines & line) != CardLines.None ? TextDecorations.Underline : TextDecorations.None;
            }
        }
    }

    // The web's links show the hand. While the pointer stays on the card off a link, the card takes the arrow rather than
    // no cursor: without one the cursor shown stays as it is until the pointer reaches another element
    // (PlayerPage.ApplyCursor), and the hand would stay over the artwork. A card that never showed the hand sets none, and
    // none stays once the pointer is gone (DropLinks), so a page's own cursor is never held back by a card.
    private void ShowHand(bool hand)
    {
        if (hand)
        {
            SetCursor(LinkCursor.Hand);
        }
        else if (_cursor == LinkCursor.Hand || _handMayLinger)
        {
            SetCursor(LinkCursor.Arrow);
        }
    }

    private void SetCursor(LinkCursor cursor)
    {
        if (cursor == _cursor)
        {
            return;
        }
        _handMayLinger = cursor == LinkCursor.None && _cursor == LinkCursor.Hand;
        _cursor = cursor;
        ProtectedCursor = cursor switch
        {
            LinkCursor.Hand => _hand ??= InputSystemCursor.Create(InputSystemCursorShape.Hand),
            LinkCursor.Arrow => _arrow ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow),
            _ => null,
        };
    }

    // From here on the card notes its presses, and hears from the button around it when one is over.
    private void FollowPresses()
    {
        if (_headingPress is not null)
        {
            return;
        }
        _headingPress = new HeadingPress();
        _onPressed = OnPressed;
        _onHostKey = OnHostKey;
        _onHostCaptureLost = OnHostCaptureLost;
        // Also a press one of the card's own buttons took: every press forgets the one before.
        AddHandler(PointerPressedEvent, _onPressed, handledEventsToo: true);
        FollowHostPresses();
    }

    // The button around the card, found anew whenever the card is loaded (OnLoaded).
    private void FollowHostPresses()
    {
        if (_headingPress is null || _host is not { } host || ReferenceEquals(host, _pressHost))
        {
            return;
        }
        ForgetHostPresses();
        _pressHost = host;
        host.AddHandler(PreviewKeyDownEvent, _onHostKey, handledEventsToo: true);
        host.AddHandler(PointerCaptureLostEvent, _onHostCaptureLost, handledEventsToo: true);
    }

    private void ForgetHostPresses()
    {
        if (_pressHost is { } host)
        {
            _pressHost = null;
            host.RemoveHandler(PreviewKeyDownEvent, _onHostKey);
            host.RemoveHandler(PointerCaptureLostEvent, _onHostCaptureLost);
        }
        _headingPress?.Clear();
    }

    // The press is not handled here: it goes on to the button, which presses, takes focus and clicks as for any press. Only
    // the left button, a finger or a pen on a title line that opens its series is noted; any other press forgets one.
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var line = CardLines.None;
        if (LinkedCard() is { HeadingId: not null })
        {
            var point = e.GetCurrentPoint(CaptionPanel);
            if (point.Properties.IsLeftButtonPressed && LineAt(point.Position) is (var pressed, CardLines.None))
            {
                line = pressed;
            }
        }
        _headingPress?.Begin(Card, line, Pick is not null);
    }

    // A key on the way to the button, before it clicks: Enter or a remote's Select never opens the series by a press that
    // is still noted.
    private void OnHostKey(object sender, KeyRoutedEventArgs e) => _headingPress?.Clear();

    // Every press ends with the button letting go of the pointer, with a click or without (released elsewhere, turned into
    // a scroll). Forgotten a turn later, so after the click whichever of the two the button raises first.
    private void OnHostCaptureLost(object sender, PointerRoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => _headingPress?.Clear());
}
