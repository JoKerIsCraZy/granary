using System.Windows.Input;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// Any catalog item; the navigation parameter is its content id (<see cref="Core.Abstractions.AppRoute.Item"/>). Its Download
/// items follow the app's downloads only while it shows: from <see cref="OnNavigatedTo"/> until it is left or unloads, so the
/// downloads, which live as long as the app, never hold it (<c>ItemDetailDownloadsTests</c>).
/// </summary>
public sealed partial class ItemDetailPage : Page, IArrivalFocus, ICardMenuHost
{
    public ItemDetailPage()
    {
        // Transient and not disposable: the root provider does not keep it alive.
        ViewModel = App.Services.GetRequiredService<ItemDetailViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public ItemDetailViewModel ViewModel { get; }

    Control? IArrivalFocus.ArrivalFocus => Hero.ArrivalFocus;

    // A season's episode cards get Download, or their download's status and Delete download, in their ⋯ menu.
    ICardMenuSource? ICardMenuHost.CardMenu => ViewModel;

    /// <summary>A remote's Play anywhere on the page plays the title as its Play does; false while it cannot.</summary>
    internal bool PlayFromKey()
    {
        if (!ViewModel.PlayCommand.CanExecute(null))
        {
            return false;
        }
        ViewModel.PlayCommand.Execute(null);
        return true;
    }

    // Static because x:Bind function bindings in item templates call them without the page.
    public static string SeasonName(string title, string caption, bool isWatched) => isWatched ? $"{title}, {caption}, watched" : $"{title}, {caption}";

    // The backdrop's placeholder. The view model passes "" for none rather than null: x:Bind skips a function whose
    // argument is null, and a page shown again for another title would keep the last one's picture.
    public static ImageSource? Blur(string hash) => Thumbhashes.Get(hash);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = Frame?.XamlRoot?.RasterizationScale ?? 1;
        ViewModel.LoadCommand.Execute(e.Parameter as string);
        // The spoiler switches and the viewer's state from elsewhere, while the page shows.
        ViewModel.Activate();
        ViewModel.FollowDownloads();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.LoadCommand.Cancel();
        ViewModel.Deactivate();
        ViewModel.StopFollowingDownloads();
    }

    // Also when the page goes without being navigated from. One shown again before its Unloaded came keeps following.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(Frame?.Content, this))
        {
            ViewModel.StopFollowingDownloads();
        }
    }

    private void OnBackdropOpened(object sender, RoutedEventArgs e) => Backdrop.Opacity = 1;

    private void OnSeasonClick(object sender, RoutedEventArgs e) =>
        Invoke(ViewModel.OpenCardCommand, (sender as FrameworkElement)?.DataContext as MediaCard);

    private void OnEpisodeClick(object sender, RoutedEventArgs e) =>
        Invoke(ViewModel.OpenEpisodeCommand, (sender as FrameworkElement)?.DataContext as DetailEpisode);

    private void OnPersonClick(object sender, RoutedEventArgs e) =>
        Invoke(ViewModel.OpenPersonCommand, (sender as FrameworkElement)?.DataContext as DetailPerson);

    private static void Invoke(ICommand command, object? parameter)
    {
        if (parameter is not null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    // The API names only YouTube's video key; the trailer plays over the window in YouTube's own player (TrailerOverlay).
    private void OnTrailerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailTrailer trailer })
        {
            App.Services.GetRequiredService<TrailerOverlayViewModel>().Open(trailer);
        }
    }
}
