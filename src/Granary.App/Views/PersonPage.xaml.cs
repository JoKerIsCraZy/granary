using System.ComponentModel;
using System.Globalization;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>A cast or crew member and their titles; the parameter is the person's id.</summary>
public sealed partial class PersonPage : Page
{
    // Twice the shown width, so the photo stays sharp at 200 % scaling.
    private const int PhotoDecodeWidth = 360;

    private static readonly Brush FailedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

    public PersonPage()
    {
        ViewModel = App.Services.GetRequiredService<PersonViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public PersonViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        bool isReturn = e.NavigationMode == NavigationMode.Back;
        if (!isReturn)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        try
        {
            await ViewModel.ShowAsync(e.Parameter as string, isReturn);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<PersonPage>>().LogError(ex, "A person could not be shown");
        }
    }

    // Cached, the page stays alive under the next one: its refresh reads stop until it is shown again.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Pause();
    }

    /// <summary>The facts as the web's badges write them: in capitals.</summary>
    public IReadOnlyList<string> Badges(IReadOnlyList<string> facts) => [.. facts.Select(f => f.ToUpper(CultureInfo.CurrentCulture))];

    public Brush NoticeBrush(bool failed) => failed ? FailedBrush : (Brush)Application.Current.Resources["SiloMutedBrush"];

    private void OnFilterChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, CultureInfo.InvariantCulture, out int index)
            && index < ViewModel.Filters.Count)
        {
            ViewModel.Filter = ViewModel.Filters[index];
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PersonViewModel.PhotoUrl):
                ShowPhoto();
                break;
            // Another person starts on All.
            case nameof(PersonViewModel.Filter):
                var shown = ViewModel.Filters.ToList().IndexOf(ViewModel.Filter) switch
                {
                    1 => MoviesFilter,
                    2 => SeriesFilter,
                    _ => AllFilter,
                };
                shown.IsChecked = true;
                break;
        }
    }

    // A photo that fails or is missing leaves the initials; the next person's never shows the last one's.
    private void ShowPhoto() => Photo.Source = Artwork.Load(ViewModel.PhotoUrl, PhotoDecodeWidth);

    private void OnPhotoFailed(object sender, ExceptionRoutedEventArgs e) => Photo.Source = null;
}
