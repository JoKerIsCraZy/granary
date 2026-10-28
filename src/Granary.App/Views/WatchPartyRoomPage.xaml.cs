using Granary.App.Controls;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Granary.App.Views;

/// <summary>The Watch Party this profile is in.</summary>
public sealed partial class WatchPartyRoomPage : Page
{
    private const int PosterWidth = 120;
    private const int SmallPosterWidth = 64;
    private const int BackdropWidth = 1280;

    // The web's member colours (room/members.ts): Tailwind's indigo, rose, teal, amber, violet, sky, lime and orange; a
    // quarter of the 500 shade behind the 200 shade for avatars, the 700 shade for the picker's solid badges.
    private static readonly (uint Mid, uint Light, uint Deep)[] Tints =
    [
        (0x6366F1, 0xC7D2FE, 0x4338CA), (0xF43F5E, 0xFECDD3, 0xBE123C), (0x14B8A6, 0x99F6E4, 0x0F766E), (0xF59E0B, 0xFDE68A, 0xB45309),
        (0x8B5CF6, 0xDDD6FE, 0x6D28D9), (0x0EA5E9, 0xBAE6FD, 0x0369A1), (0x84CC16, 0xD9F99D, 0x4D7C0F), (0xF97316, 0xFED7AA, 0xC2410C),
    ];

    public WatchPartyRoomPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchPartyRoomViewModel>();
        InitializeComponent();
    }

    public WatchPartyRoomViewModel ViewModel { get; }

    // Static because x:Bind function bindings in the templates call them on the type.
    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static BitmapImage? SmallPoster(Uri? url) => Artwork.Load(url, SmallPosterWidth);

    public static BitmapImage? Backdrop(Uri? url) => Artwork.Load(url, BackdropWidth);

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Live(bool hasRoom, bool ended) => hasRoom && !ended ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Both(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static string Upper(string text) => text.ToUpperInvariant();

    public static string Hint(string hint) => "· " + hint;

    public static string EndedTitle(bool replaced) => replaced ? "Watch Party joined on another device" : "This watch party has ended";

    public static string CopyCodeName(string code) => $"Copy room code {code}";

    /// <summary>"Host picks · you".</summary>
    public static string ModeMenuLabel(string mode) => mode + " · you";

    public static object? Tip(string names) => names.Length > 0 ? names : null;

    public static Brush ConnectionDot(bool live) => Solid(live ? 0xFF34D399 : 0x40FFFFFF);

    public static Brush MemberDot(bool waiting) => Solid(waiting ? 0xFFFBBF24 : 0xFF34D399);

    /// <summary>The hero's eyebrow: green while playing, amber for a leader with votes, muted otherwise.</summary>
    public static Brush ToneBrush(string tone) => tone switch
    {
        "live" => Solid(0xE634D399),
        "leading" => Solid(0xE6FCD34D),
        _ => (Brush)Application.Current.Resources["SiloMutedBrush"],
    };

    public static Brush VoteBackground(bool voted) => voted ? (Brush)Application.Current.Resources["SiloPrimaryBrush"] : Solid(0x00000000);

    public static Brush VoteBorder(bool voted) => voted ? (Brush)Application.Current.Resources["SiloPrimaryBrush"] : Solid(0x40FFFFFF);

    public static double Shown(bool value) => value ? 1 : 0;

    public static Brush Ring(bool selected) => selected ? (Brush)Application.Current.Resources["SiloPrimaryBrush"] : Solid(0x00000000);

    public static Brush TintBackground(int tint) => Solid(0x40000000 | Tints[Index(tint)].Mid);

    public static Brush TintForeground(int tint) => Solid(0xFF000000 | Tints[Index(tint)].Light);

    public static Brush SolidTint(int tint) => Solid(0xFF000000 | Tints[Index(tint)].Deep);

    private static int Index(int tint) => ((tint % Tints.Length) + Tints.Length) % Tints.Length;

    private static SolidColorBrush Solid(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

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

    private void OnRailTabChecked(object sender, RoutedEventArgs e)
    {
        bool activity = ReferenceEquals(sender, ActivityTab);
        PeoplePanel.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        ActivityPanel.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Code.Length > 0 && Copy(ViewModel.Code))
        {
            ViewModel.Copied($"Room code {ViewModel.Code} copied");
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.InviteLink is not { } link)
        {
            ViewModel.Copied("Invite link isn't ready yet");
            return;
        }
        if (Copy(link.AbsoluteUri))
        {
            ViewModel.Copied("Invite link copied");
        }
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchPartyShelfCard card })
        {
            ViewModel.Shelf.ChooseCommand.Execute(card);
            Scroller.ChangeView(null, 0, null);
        }
    }

    private void OnVoteClick(object sender, RoutedEventArgs e) => Run(sender, ViewModel.VoteCommand);

    private void OnPromoteClick(object sender, RoutedEventArgs e) => Run(sender, ViewModel.PromoteCommand);

    private void OnDeleteSuggestionClick(object sender, RoutedEventArgs e) => Run(sender, ViewModel.DeleteSuggestionCommand);

    private static void Run(object sender, System.Windows.Input.ICommand command)
    {
        if (sender is FrameworkElement { Tag: WatchPartySuggestionRow row } && command.CanExecute(row))
        {
            command.Execute(row);
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

    // The code and the link are secrets of the room: they go to the clipboard only, never to a log, nor to the
    // clipboard's history or other devices.
    private static bool Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            return Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<WatchPartyRoomPage>>().LogInformation("The clipboard could not be written: {Failure}", ex.GetType().Name);
            return false;
        }
    }
}
