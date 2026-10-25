using System.Numerics;
using Granary.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Hosting;

namespace Granary.App.Views;

// The skip pill's confirmation ("Intro skipped"): read out when its pill appears, and shaded so that it stays readable
// where the picture behind it is bright. The web player's drop shadow is fainter; on a white corner its line disappears.
public sealed partial class PlayerPage
{
    private void InitializeSkipPrompt()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(SkipCaptionText).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Mask = SkipCaptionText.GetAlphaMask();
        shadow.Color = Colors.Black;
        shadow.Opacity = 0.85f;
        shadow.BlurRadius = 6;
        shadow.Offset = new Vector3(0, 1, 0);
        var visual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        SkipCaptionText.SizeChanged += (_, e) => visual.Size = e.NewSize.ToVector2();
        ElementCompositionPreview.SetElementChildVisual(SkipCaptionShade, visual);
    }

    private void AnnounceSkipCaption() => Announcer.Announce(this, ViewModel.SkipCaption, "SkipPrompt");
}
