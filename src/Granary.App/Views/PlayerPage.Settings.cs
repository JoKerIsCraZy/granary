using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// The settings button: the web player's name for it, with the speed while it is not 1×; and, Granary's own, the dot in
// the gear that says how the stream plays (PlayerViewModel.StreamMethod), named for screen readers and in the tooltip
// (SettingsButtonText).
public sealed partial class PlayerPage
{
    // The theme's two dot brushes, looked up once.
    private Brush? _streamDirectBrush;
    private Brush? _streamTranscodeBrush;

    // Instance methods because x:Bind function bindings call them on the page; one per target type.

    public string SettingsName(string speedBadge, StreamMethod method) => SettingsButtonText.Name(speedBadge, method);

    public string SettingsTip(StreamMethod method) => SettingsButtonText.Tip(method);

    public Visibility ShowsStreamMethod(StreamMethod method) => method == StreamMethod.None ? Visibility.Collapsed : Visibility.Visible;

    public Brush? StreamMethodBrush(StreamMethod method) => method switch
    {
        StreamMethod.DirectPlay or StreamMethod.DirectStream => _streamDirectBrush ??= ThemeBrush("SiloStreamDirectBrush"),
        StreamMethod.Transcode => _streamTranscodeBrush ??= ThemeBrush("SiloStreamTranscodeBrush"),
        _ => null,
    };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];
}
