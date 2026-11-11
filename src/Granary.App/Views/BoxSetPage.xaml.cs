using Granary.App.Controls;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>A movie box set; the parameter is a <see cref="BoxSetRouteParameter"/>.</summary>
public sealed partial class BoxSetPage : Page, IArrivalFocus
{
    public BoxSetPage()
    {
        // Transient and not disposable: the root provider does not keep it alive.
        ViewModel = App.Services.GetRequiredService<BoxSetViewModel>();
        InitializeComponent();
    }

    public BoxSetViewModel ViewModel { get; }

    // Play, as a title's page starts on its Play.
    Control? IArrivalFocus.ArrivalFocus => PlayButton;

    /// <summary>A remote's Play anywhere on the page plays the film to watch next, as Play does; false while it cannot.</summary>
    internal bool PlayFromKey()
    {
        if (!ViewModel.PlayCommand.CanExecute(null))
        {
            return false;
        }
        ViewModel.PlayCommand.Execute(null);
        return true;
    }

    // Static because x:Bind function bindings call it with "" for none: one skipped for null would keep a picture.
    public static ImageSource? Blur(string hash) => Thumbhashes.Get(hash);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = Frame?.XamlRoot?.RasterizationScale ?? 1;
        ViewModel.LoadCommand.Execute(e.Parameter as BoxSetRouteParameter);
        // The viewer's state from elsewhere, while the page shows.
        ViewModel.Activate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
    }
}
