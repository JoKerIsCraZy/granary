using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

// A Continue Watching card's "NEW SEASON" after its caption (MediaCard.CaptionBadge), as the web's ContinueWatchingCard. It is
// one of the caption's details, so the profile's "Title" and "Nothing" leave it out with the time left. Only a card that has
// one makes its line, which then stands in for CaptionText; a recycled card without one shows CaptionText again.
public sealed partial class MediaCardView
{
    // The web's gap-1.5 between the time left and the pill.
    private const double CaptionBadgeGap = 6;

    private void ShowCaptionText(string? caption, string? badge)
    {
        bool badged = !string.IsNullOrEmpty(badge);
        SetText(CaptionText, badged ? null : caption);
        if (badged && BadgedCaption is null)
        {
            FindName(nameof(BadgedCaption));
        }
        if (BadgedCaption is not { } line)
        {
            return;
        }
        line.Visibility = Visible(badged);
        if (badged)
        {
            SetText(BadgedCaptionText, caption);
            CaptionBadgeText.Text = badge;
            // Alone, as on a Next Up card, it starts where the text would.
            CaptionBadge.Margin = new Thickness(string.IsNullOrEmpty(caption) ? 0 : CaptionBadgeGap, 0, 0, 0);
        }
    }
}
