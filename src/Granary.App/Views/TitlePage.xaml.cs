using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>A title from TMDB; the parameter is a <see cref="TitleRouteParameter"/>.</summary>
public sealed partial class TitlePage : Page
{
    public TitlePage()
    {
        ViewModel = App.Services.GetRequiredService<TitleViewModel>();
        InitializeComponent();
    }

    public TitleViewModel ViewModel { get; }

    /// <summary>"Request seasons" while it is open.</summary>
    public RequestSeasonsViewModel? Picker { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.ShowAsync(e.Parameter as TitleRouteParameter);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            Logger().LogError(ex, "A title could not be shown");
        }
    }

    // Segoe Fluent Icons: Add, Library, Accept (a state).
    public static string PrimaryGlyph(TitlePrimaryAction action) => action switch
    {
        TitlePrimaryAction.Request => "",
        TitlePrimaryAction.OpenInLibrary => "",
        _ => "",
    };

    // Ringer, RingerOff.
    public static string BellGlyph(bool isFollowing) => isFollowing ? "" : "";

    public static Visibility HasLink(Uri? url) => url is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Any(IReadOnlyList<DetailRating> ratings) => ratings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static double Percent(double? percent) => percent ?? 0;

    public static InfoBarSeverity NoticeSeverity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    // Instance helper because x:Bind function bindings call it on the page.
    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private static ILogger Logger() => App.Services.GetRequiredService<ILogger<TitlePage>>();

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (ViewModel.PrimaryAction)
            {
                case TitlePrimaryAction.OpenInLibrary:
                    ViewModel.OpenInLibraryCommand.Execute(null);
                    break;
                case TitlePrimaryAction.Request when ViewModel.NeedsSeasonPicker:
                    await PickSeasonsAsync();
                    break;
                case TitlePrimaryAction.Request:
                    await ViewModel.RequestCommand.ExecuteAsync(null);
                    break;
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed request is already shown by the page.
            Logger().LogError(ex, "A title could not be requested");
        }
    }

    // A series with seasons asks which ones first, as the web's dialog; one dialog at a time.
    private async Task PickSeasonsAsync()
    {
        if (Picker is not null || ViewModel.SeasonPicker() is not { } picker)
        {
            return;
        }
        Picker = picker;
        try
        {
            Bindings.Update();
            if (await SeasonsDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.RequestSeasonsAsync(picker);
            }
        }
        finally
        {
            Picker = null;
        }
    }

    // Withdrawing asks first, as the web's dialog does.
    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = "Cancel this request?",
                Content = $"Your request for \"{ViewModel.Title}\" will be withdrawn before it is sent to the download automation.",
                PrimaryButtonText = "Cancel request",
                CloseButtonText = "Keep request",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.CancelCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed cancellation is already shown by the page.
            Logger().LogError(ex, "A request could not be cancelled");
        }
    }
}
