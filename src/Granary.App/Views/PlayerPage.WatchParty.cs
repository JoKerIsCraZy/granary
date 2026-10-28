using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.App.Services;
using Granary.Core.WatchParty;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// The Watch Party panel (PartyPanel), as the web player's WatchTogetherPanel: Invite copies the link, End asks first
// (the view model, through IConfirmation). What they did shows as the player's notice. Faded, its buttons leave the tab
// order with it.
public sealed partial class PlayerPage
{
    private void InitializePartyPanel()
    {
        if (ViewModel.Party is { } party)
        {
            party.EndCommand.PropertyChanged += OnPartyEndChanged;
        }
    }

    private void StopPartyPanel()
    {
        if (ViewModel.Party is { } party)
        {
            party.EndCommand.PropertyChanged -= OnPartyEndChanged;
        }
    }

    private void OnPartyInviteClick(object sender, RoutedEventArgs e) => ViewModel.Party?.CopyInvite(RoomClipboard.Copy);

    // The question gives focus back to End when it closes: the page takes it, or a focused End would hold the controls
    // up for good.
    private void OnPartyEndChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning) && ViewModel.Party?.EndCommand.IsRunning == false
            && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsWithin(focused, PartyPanel))
        {
            Focus(FocusState.Programmatic);
        }
    }

    // A faded panel takes no keyboard focus, as it takes no clicks.
    private void ShowPartyPanel(bool visible)
    {
        PartyPanel.Opacity = visible ? 1 : 0;
        PartyPanel.IsHitTestVisible = visible;
        PartyInviteButton.IsTabStop = PartyPolicyButton.IsTabStop = PartyStopButton.IsTabStop = PartyEndButton.IsTabStop = visible;
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
