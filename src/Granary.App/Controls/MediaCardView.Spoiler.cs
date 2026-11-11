using System.Diagnostics;
using System.Numerics;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

// An unwatched episode's still the profile hides (MediaCard.BlursImage), as the web's scale-110 blur-xl in place of the
// pointer's zoom: blurred (SpoilerArtwork) and grown a tenth, so the blur fills the card to its rounded edges. The card never
// decodes the sharp picture then. Only a card that shows such a still makes the image, and a recycled card keeps it.
public sealed partial class MediaCardView
{
    // The web's scale-110.
    private const float SpoilerScale = 1.1f;

    private bool _blurs;

    private void ShowSpoiler(MediaCard? card)
    {
        _blurs = card?.BlursImage == true;
        if (_blurs && SpoilerImage is null)
        {
            FindName(nameof(SpoilerImage));
            SpoilerImage!.Scale = new Vector3(SpoilerScale, SpoilerScale, 1);
            CenterSpoiler();
        }
        if (SpoilerImage is { } image)
        {
            image.OpacityTransition = null;
            image.Opacity = 0;
            image.Source = null;
            image.Visibility = Visible(_blurs);
        }
    }

    private void CenterSpoiler()
    {
        if (SpoilerImage is { } image && _artWidth > 0)
        {
            image.CenterPoint = new Vector3((float)(_artWidth / 2), (float)(Art.Height / 2), 0);
        }
    }

    // As the artwork shows: at once when it came at once, else fading in over the thumbhash's blur.
    private async Task LoadSpoilerAsync(Uri url, CancellationToken cancellationToken)
    {
        var picture = await SpoilerArtwork.LoadAsync(url, _artWidth, cancellationToken);
        if (cancellationToken.IsCancellationRequested || picture is null || SpoilerImage is not { } image)
        {
            return;
        }
        image.Source = picture;
        Placeholder.Visibility = Visibility.Collapsed;
        bool waited = Stopwatch.GetElapsedTime(_artworkShownAt) > InstantReveal;
        image.OpacityTransition = waited ? new ScalarTransition { Duration = RevealDuration } : null;
        image.Opacity = 1;
    }
}
