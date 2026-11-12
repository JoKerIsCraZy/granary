using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A row that is only read: it takes keyboard focus, so a TV remote's arrows walk a panel's rows and its scroller follows
/// them, and a screen reader says its <c>AutomationProperties.Name</c> as one text. It does nothing when picked.
/// </summary>
public sealed partial class ReadingRow : ContentControl
{
    public ReadingRow()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ReadingRowAutomationPeer(this);

    private sealed partial class ReadingRowAutomationPeer(ReadingRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(ReadingRow);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
