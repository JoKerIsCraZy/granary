using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// The settings button: the web player's name for it, with the speed while it is not 1×; and, Granary's own, the dot in
// the gear that says how the stream plays (PlayerViewModel.StreamMethod), named for screen readers and in the tooltip.
public sealed partial class PlayerPage
{
    private const string SettingsText = "Playback settings";

    // Instance methods because x:Bind function bindings call them on the page; one per target type.

    /// <summary>"Playback settings, Direct Play, 1.5×": the method while a server stream plays, the speed while it is not 1×.</summary>
    public string SettingsName(string speedBadge, StreamMethod method) => Join(SettingsText, StreamMethods.Label(method), speedBadge);

    /// <summary>"Playback settings, Transcode"; just "Playback settings" without a server stream.</summary>
    public string SettingsTip(StreamMethod method) => Join(SettingsText, StreamMethods.Label(method));

    public Visibility ShowsStreamMethod(StreamMethod method) => method == StreamMethod.None ? Visibility.Collapsed : Visibility.Visible;

    public Brush? StreamMethodBrush(StreamMethod method) => method switch
    {
        StreamMethod.DirectPlay or StreamMethod.DirectStream => (Brush)Application.Current.Resources["SiloStreamDirectBrush"],
        StreamMethod.Transcode => (Brush)Application.Current.Resources["SiloStreamTranscodeBrush"],
        _ => null,
    };

    private static string Join(params string[] parts) => string.Join(", ", parts.Where(part => part.Length > 0));
}
