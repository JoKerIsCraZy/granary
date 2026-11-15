using System.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.Remote;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Granary.App;

// What the window shows while Silo's phone app controls this PC (design 5.9, RemoteIndicatorViewModel): the short
// "connected" notice, bound in MainWindow.xaml, and the quiet indicator beside the activity indicator, shown here. It
// shows as the activity indicator does: over the pages, and over them while the player is its bar, never over the full
// player, whose bar has an icon of its own. No standby screen: a PC is used for other things. A title a phone starts
// opens as any other, and its player says who started it; the window is not brought to the front.
public sealed partial class MainWindow
{
    private readonly RemoteReceiver _receiver;

    // The title a phone started last, with the phone's name, until the player that opens it is made: the receiver says so
    // just before it opens the player.
    private RemoteLaunch? _launched;

    /// <summary>The phone remote's notice and indicator, for the app.</summary>
    public RemoteIndicatorViewModel Remote { get; }

    public string RemoteActiveText => RemoteIndicatorViewModel.ActiveText;

    public string RemoteDisconnectText => RemoteIndicatorViewModel.DisconnectText;

    private void InitializePhoneRemote()
    {
        Remote.PropertyChanged += OnRemoteChanged;
        _receiver.Launched += OnLaunched;
        // A phone's title that opened while the window was away goes full screen once the viewer brings it back.
        Activated += OnActivatedForPhoneTitle;
        Closed += (_, _) =>
        {
            Remote.PropertyChanged -= OnRemoteChanged;
            _receiver.Launched -= OnLaunched;
            Activated -= OnActivatedForPhoneTitle;
        };
        ShowRemoteIndicator();
    }

    private void OnLaunched(object? sender, RemoteLaunch launch) => _launched = launch;

    private void OnActivatedForPhoneTitle(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _player?.WindowCameToFront();
        }
    }

    // The phone's name when a phone started that very request, once; null for any other title.
    private string? TakeLaunch(PlayRequest play)
    {
        if (_launched is not { } launch || !ReferenceEquals(launch.Request, play))
        {
            return null;
        }
        _launched = null;
        return launch.PhoneName;
    }

    private void OnRemoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteIndicatorViewModel.IsActive))
        {
            ShowRemoteIndicator();
        }
    }

    private void ShowRemoteIndicator()
    {
        // The app is hidden under the full player (ShowApp), and the indicator with it.
        bool shown = Remote.IsActive && Shell.IsSignedIn && Navigation.Visibility == Visibility.Visible;
        if (!shown)
        {
            RemoteIndicator.Flyout?.Hide();
        }
        RemoteIndicator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // Disconnect Remote: every phone that controls this PC is let go.
    private async void OnDisconnectRemoteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Remote.DisconnectCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. The receiver logs a connection's end itself.
            _logger.LogWarning("Disconnect Remote failed ({Error})", ex.GetType().Name);
        }
    }
}
