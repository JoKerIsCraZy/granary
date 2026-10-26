using System.ComponentModel;
using System.Windows.Input;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

/// <summary>Any catalog item; the navigation parameter is its content id (<see cref="Core.Abstractions.AppRoute.Item"/>).</summary>
public sealed partial class ItemDetailPage : Page
{
    // The hero fills most of the window as on the web, never less than this: 60 % of the height (the web's below its lg
    // width; its 72 % above put the title low on a wide screen), a season's 50 %.
    private const double HeroShare = 0.5;
    private const double CompactHeroShare = 0.5;
    private const double MinimumHeroHeight = 440;

    public ItemDetailPage()
    {
        // Transient and not disposable: the root provider does not keep it alive.
        ViewModel = App.Services.GetRequiredService<ItemDetailViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ItemDetailViewModel ViewModel { get; }

    // Static because x:Bind function bindings in item templates call them without the page.
    public static string SeasonName(string title, string caption, bool isWatched) => isWatched ? $"{title}, {caption}, watched" : $"{title}, {caption}";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = Frame?.XamlRoot?.RasterizationScale ?? 1;
        ViewModel.LoadCommand.Execute(e.Parameter as string);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.LoadCommand.Cancel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemDetailViewModel.IsCompactHero))
        {
            UpdateHeroHeight(ActualHeight);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeroHeight(e.NewSize.Height);

    private void UpdateHeroHeight(double pageHeight)
    {
        if (pageHeight > 0)
        {
            Hero.MinHeight = Math.Max(MinimumHeroHeight, pageHeight * (ViewModel.IsCompactHero ? CompactHeroShare : HeroShare));
        }
    }

    private void OnSeasonClick(object sender, RoutedEventArgs e) =>
        Invoke(ViewModel.OpenCardCommand, (sender as FrameworkElement)?.DataContext as MediaCard);

    private void OnEpisodeClick(object sender, RoutedEventArgs e) =>
        Invoke(ViewModel.OpenEpisodeCommand, (sender as FrameworkElement)?.DataContext as DetailEpisode);

    private static void Invoke(ICommand command, object? parameter)
    {
        if (parameter is not null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    // The API names only YouTube's video key; the trailer plays on YouTube in the browser.
    private async void OnTrailerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DetailTrailer trailer })
        {
            return;
        }
        try
        {
            if (!await Launcher.LaunchUriAsync(trailer.WatchUrl))
            {
                App.Services.GetRequiredService<ILogger<ItemDetailPage>>().LogWarning("The browser did not open the trailer");
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ItemDetailPage>>().LogWarning("The trailer could not be opened: {Error}", ex.GetType().Name);
        }
    }
}
