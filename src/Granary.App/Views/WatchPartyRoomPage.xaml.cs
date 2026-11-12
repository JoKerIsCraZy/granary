using Granary.App.Controls;
using Granary.App.Services;
using Granary.Core.Texts;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
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

    public static Visibility Both(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>An overline in capitals, by the rules of the language on screen.</summary>
    public static string Upper(string text) => AppText.Upper(text);

    /// <summary>A shelf row's hint after its title: "· anyone in the room".</summary>
    public static string Hint(string hint) => AppText.Get("web.watchTogether.picker.rowHint", ("hint", hint));

    public static string CopyCodeName(string code) => AppText.Get("web.watchTogether.strip.copyCode", ("code", code));

    // Words of the templates, read where each element is made.
    public static string SeriesPill => AppText.Get("web.watchTogether.common.series");

    public static string VoteMark => AppText.Get("web.watchTogether.suggestions.voteMark");

    public static string LeadingBadge => AppText.Upper(AppText.Get("web.watchTogether.suggestions.leading"));

    public static string HostBadge => AppText.Upper(AppText.Get("watchParty.seat.host"));

    public static string YouMark => AppText.Get("web.watchTogether.rail.you");

    public static string OnWatchlistName => AppText.Get("web.watchTogether.movie.onWatchlist");

    // The page's overlines.
    public string WhoSeenHeading => AppText.Upper(AppText.Get("watchParty.whoSeen.title"));

    public string VotingOpenHeading => AppText.Upper(AppText.Get("web.watchTogether.voting.open"));

    public string RoomOpenHeading => AppText.Upper(AppText.Get("web.watchTogether.lobby.roomOpen"));

    public string InviteHeading => AppText.Upper(AppText.Get("web.watchTogether.inviteBar.label"));

    public string SearchResultsHeading => AppText.Upper(AppText.Get("web.watchTogether.browse.searchResults"));

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
        // A room just started from a title's page says what happened on the way; Back to the page later does not again.
        if (e.NavigationMode == NavigationMode.New && e.Parameter is WatchPartyArrival arrival)
        {
            ViewModel.Arrive(arrival);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
    }

    private void OnRailTabChecked(object sender, RoutedEventArgs e)
    {
        // People starts checked, so this runs while the page is still being built, before the panels exist; they
        // start as People shows them.
        if (PeoplePanel is null || ActivityPanel is null)
        {
            return;
        }
        bool activity = ReferenceEquals(sender, ActivityTab);
        PeoplePanel.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        ActivityPanel.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Code.Length > 0 && RoomClipboard.Copy(ViewModel.Code))
        {
            ViewModel.Copied(AppText.Get("web.watchTogether.common.codeCopied", ("code", ViewModel.Code)));
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.InviteLink is not { } link)
        {
            ViewModel.Copied(AppText.Get("web.watchTogether.room.inviteNotReady"));
            return;
        }
        if (RoomClipboard.Copy(link.AbsoluteUri))
        {
            ViewModel.Copied(WatchPartyText.InviteCopied(ViewModel.Code));
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
            Title = AppText.Get("web.watchTogether.endDialog.title"),
            Content = new TextBlock { Text = AppText.Get("web.watchTogether.endDialog.description"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = AppText.Get("watchParty.end.confirm"),
            CloseButtonText = AppText.Get("common.cancel"),
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
}
