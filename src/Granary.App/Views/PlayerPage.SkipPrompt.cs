using System.Numerics;
using Granary.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

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

    // A prompt that appears takes focus, as the intro pill of Silo's TV app does (TvIntroAutoSkipBanner's autoFocus): the
    // skip pill, or the next episode's Play Now. Enter, a remote's OK and a game controller's A then press what the viewer
    // sees focus on. Asked a turn later, once the prompt shows.
    private void FocusPromptSoon() => DispatcherQueue.TryEnqueue(() => FocusPrompt(fromControls: false));

    // Only from the picture: a control the viewer is on, an open menu, a panel and the marker editor keep focus, so OK
    // there never turns into a skip; the prompt gets it when the controls hide (RestFocus). A control of the hidden bar
    // is none the viewer is on: one the mouse pressed keeps focus as the bar hides, and Enter would press it unseen. Not
    // while a seek is being made either, as the TV app's mayTakeFocus. Focus shows its ring after a key or a pad, not
    // after the mouse.
    private bool FocusPrompt(bool fromControls)
    {
        if (!IsLoaded || IsCompact || ShowsNextUpScreen || XamlRoot is not { } root || HasOpenMenu() || IsPanelOpen
            || SeekBar.IsDragging || _padSeekOrigin is not null)
        {
            return false;
        }
        if (!fromControls && FocusManager.GetFocusedElement(root) is DependencyObject focused && !ReferenceEquals(focused, this)
            && IsWithin(focused, this) && IsShown(focused) && !IsInHiddenControls(focused))
        {
            return false;
        }
        ButtonBase? prompt = ViewModel.HasSkipPrompt ? SkipButton : ViewModel.IsNextUpCardShown ? NextUpPlayButton : null;
        return prompt is not null && prompt.Focus(_lastInputWasKey ? FocusState.Keyboard : FocusState.Programmatic);
    }

    // The bar hides by its look alone (SetControlsVisible), so what is in it still counts as shown.
    private bool IsInHiddenControls(DependencyObject element) => !_areControlsVisible && element is Control control && IsInControls(control);

    // Focus leaves the controls for where it rests: a prompt that shows, else the picture.
    private void RestFocus()
    {
        if (!FocusPrompt(fromControls: true))
        {
            Focus(FocusState.Programmatic);
        }
    }

    // The focused button of the skip pill or of the next episode's card. The arrows and a pad's D-pad go on as on the
    // picture while it has focus: Left and Right seek, Up brings the bar.
    private ButtonBase? FocusedPrompt() =>
        FocusManager.GetFocusedElement(XamlRoot) is ButtonBase button && (IsWithin(button, SkipPrompt) || IsWithin(button, NextUpCard))
            ? button
            : null;
}
