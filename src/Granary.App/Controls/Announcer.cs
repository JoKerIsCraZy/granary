using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Granary.App.Controls;

/// <summary>
/// Reads a message out to screen readers. A live region only speaks while its element is shown, so a message whose
/// box appears together with it ("Wrong username or password.") would go unheard; a notification does not depend on that.
/// </summary>
public static class Announcer
{
    /// <param name="activityId">Messages with the same id replace each other in the reader's queue.</param>
    public static void Announce(UIElement source, string message, string activityId)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }
        var peer = FrameworkElementAutomationPeer.FromElement(source) ?? FrameworkElementAutomationPeer.CreatePeerForElement(source);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            message, activityId);
    }
}
