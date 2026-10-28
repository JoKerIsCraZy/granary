using Granary.App.Services;
using Granary.Core.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// The Watch Party panel (PartyPanel), as the web player's WatchTogetherPanel: Invite copies the link, End asks first in the
// web's words. What either did shows as the player's notice.
public sealed partial class PlayerPage
{
    private void OnPartyInviteClick(object sender, RoutedEventArgs e) => ViewModel.Party?.CopyInvite(RoomClipboard.Copy);

    private async void OnPartyEndClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Party is not { } party)
        {
            return;
        }
        // The web's EndWatchPartyDialog.
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "End watch party?",
            Content = new TextBlock { Text = "End the watch party for everyone?", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "End Party",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await party.EndRoomAsync();
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A refused end is the panel's to say; this is anything else.
            App.Services.GetRequiredService<ILogger<PlayerPage>>().LogError(ex, "The Watch Party could not be ended from the player");
        }
    }

    // Under Exit on the right, as the playback info sits under Minimize on the left.
    public Thickness PartyPanelMargin(bool isFullScreen) => new(0, (isFullScreen ? 24 : TitleBarHeight + 8) + 48, 24, 0);

    // The web's dot: emerald while connected, amber while connecting, red when the room is gone.
    public Brush PartyDot(WatchPartyConnectionState state) => new SolidColorBrush(state switch
    {
        WatchPartyConnectionState.Connected => Windows.UI.Color.FromArgb(0xFF, 0x34, 0xD3, 0x99),
        WatchPartyConnectionState.Connecting => Windows.UI.Color.FromArgb(0xFF, 0xFC, 0xD3, 0x4D),
        _ => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
    });
}
