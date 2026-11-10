using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

// A Continue Watching card's "NEW SEASON" after its caption (MediaCard.CaptionBadge), as the web's ContinueWatchingCard. It is
// one of the caption's details, so the profile's "Title" and "Nothing" leave it out with the time left. Only a card that has
// one makes the pill; a recycled card hides it.
public sealed partial class MediaCardView
{
    // The web's gap-1.5 between the time left and the pill.
    private const double CaptionBadgeGap = 6;

    private void ShowCaptionBadge(string? badge)
    {
        bool shows = !string.IsNullOrEmpty(badge);
        bool hasText = CaptionText.Visibility == Visibility.Visible;
        if (shows && CaptionBadge is null)
        {
            FindName(nameof(CaptionBadge));
        }
        if (CaptionBadge is { } pill)
        {
            CaptionBadgeText.Text = badge ?? "";
            pill.Visibility = Visible(shows);
            // Alone, as on a Next Up card, it starts where the text would.
            pill.Margin = new Thickness(hasText ? CaptionBadgeGap : 0, 0, 0, 0);
        }
        // An empty line would still take the caption panel's spacing.
        CaptionLine.Visibility = Visible(shows || hasText);
    }
}
