using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A text box without the box's own ✕, for a search box that draws its own clear button, shown whenever it holds text as
/// the web's is (the settings' search). The box's own shows only while it has focus and would stand beside it, and a screen
/// reader would hear two of them; it is kept out of sight, of the pointer and of screen readers on every template the box
/// takes, also one applied again.
/// </summary>
public sealed partial class SearchField : TextBox
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("DeleteButton") is Button own)
        {
            own.Width = 0;
            own.MinWidth = 0;
            own.Opacity = 0;
            own.IsHitTestVisible = false;
            own.IsTabStop = false;
            AutomationProperties.SetAccessibilityView(own, AccessibilityView.Raw);
        }
    }
}
