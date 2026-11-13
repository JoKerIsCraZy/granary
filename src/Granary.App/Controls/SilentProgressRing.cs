using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A spinner that is only drawn, beside a text that already says what goes on ("Running", "Scanning…"): a screen reader
/// is told nothing of it. WinUI's own ring announces itself as a progress bar named "Busy", and
/// <c>AutomationProperties.AccessibilityView="Raw"</c> does not last on it, neither from a style nor on the element: in
/// the running app it reads back as Content, set again by the ring. So the peer says it instead.
/// </summary>
public sealed partial class SilentProgressRing : ProgressRing
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SilentPeer(this);

    private sealed partial class SilentPeer(SilentProgressRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetNameCore() => "";

        // The ring's animation inside has a peer of its own.
        protected override IList<AutomationPeer>? GetChildrenCore() => null;
    }
}
