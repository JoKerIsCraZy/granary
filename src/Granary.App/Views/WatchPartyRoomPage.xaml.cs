using Granary.App.Controls;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Granary.App.Views;

/// <summary>The Watch Party this profile is in.</summary>
public sealed partial class WatchPartyRoomPage : Page
{
    private const int PosterWidth = 120;

    public WatchPartyRoomPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchPartyRoomViewModel>();
        InitializeComponent();
    }

    public WatchPartyRoomViewModel ViewModel { get; }

    // Static because x:Bind function bindings in the templates call them on the type.
    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Live(bool hasRoom, bool ended) => hasRoom && !ended ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Either(bool first, bool second) => first || second ? Visibility.Visible : Visibility.Collapsed;

    public static string Eyebrow(bool playing, string staged) => playing ? "NOW PLAYING" : staged.ToUpperInvariant();

    public static string Caption(bool playing, string playingCaption, string stagedCaption) => playing ? playingCaption : stagedCaption;

    public static string EndedTitle(bool replaced) => replaced ? "Watch Party joined on another device" : "This watch party has ended";

    public static string QueueName(string title) => $"Queue {title}";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
    }

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Code.Length > 0 && Copy(ViewModel.Code))
        {
            ViewModel.Copied("Copied the code.");
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.InviteLink is not { } link)
        {
            ViewModel.Copied("Invite link isn't ready yet.");
            return;
        }
        if (Copy(link.AbsoluteUri))
        {
            ViewModel.Copied("Copied the invite link.");
        }
    }

    private void OnQueueClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchPartyPick pick } && ViewModel.QueueCommand.CanExecute(pick))
        {
            ViewModel.QueueCommand.Execute(pick);
        }
    }

    // The switch shows the room's policy; a flip asks the room, whose answer sets it back if it said no.
    private void OnPolicyToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.IsOn != ViewModel.GuestsCanPause)
        {
            ViewModel.ToggleGuestsCanPauseCommand.Execute(null);
        }
    }

    private async void OnEndRoomClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "End this watch party?",
            Content = new TextBlock { Text = "Everyone leaves the room and playback stops for all of them.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "End room",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.EndRoomAsync();
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A refused end is the page's to say; this is anything else.
            App.Services.GetRequiredService<ILogger<WatchPartyRoomPage>>().LogError(ex, "The Watch Party could not be ended");
        }
    }

    // The code and the link are secrets of the room: they go to the clipboard only, never to a log.
    private static bool Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<WatchPartyRoomPage>>().LogInformation("The clipboard could not be written: {Failure}", ex.GetType().Name);
            return false;
        }
    }
}
