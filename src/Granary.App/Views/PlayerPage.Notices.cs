using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Views;

// The web player's PlaybackNoticeOverlay: a message from the server or an administrator below the top bar, sky blue, or
// amber while the server is going away. The bar and the mini view keep it until the full player shows again.
public sealed partial class PlayerPage
{
    private static readonly SolidColorBrush InfoTint = new(Color.FromArgb(0x26, 0x0E, 0xA5, 0xE9));
    private static readonly SolidColorBrush InfoEdge = new(Color.FromArgb(0x80, 0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush WarningTint = new(Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush WarningEdge = new(Color.FromArgb(0x80, 0xFB, 0xBF, 0x24));

    // Instance methods because x:Bind function bindings call them on the page.
    public Brush NoticeTint(bool isWarning) => isWarning ? WarningTint : InfoTint;

    public Brush NoticeEdge(bool isWarning) => isWarning ? WarningEdge : InfoEdge;

    private void InitializeNotices() =>
        RegisterPropertyChangedCallback(IsCompactProperty, (_, _) => ViewModel.HoldNotices(IsCompact));
}
