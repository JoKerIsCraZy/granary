using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

public sealed partial class HomePage : Page
{
    private MediaCard? _menuCard;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // The page is cached: Back keeps the scroll position, a new visit starts at the top.
        if (e.NavigationMode != NavigationMode.Back)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        try
        {
            await ViewModel.ActivateAsync();
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the view model.
            App.Services.GetRequiredService<ILogger<HomePage>>().LogError(ex, "Home could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Style StatusStyle(bool hasError) =>
        (Style)Application.Current.Resources[hasError ? "SiloBodyTextStyle" : "SiloRowTitleTextStyle"];

    // The context request bubbles up from the card's button; only removable cards get the menu.
    private void OnRowsContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var target = CardButton(args.OriginalSource as DependencyObject);
        if (target?.DataContext is not MediaCard card || !ViewModel.CanRemove(card))
        {
            return;
        }
        args.Handled = true;
        _menuCard = card;
        RemoveItem.Text = RowOf(target) is { } row ? $"Remove from {row.Title}" : "Remove from Continue Watching";
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(target, out var point))
        {
            options.Position = point;
        }
        CardMenu.ShowAt(target, options);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_menuCard is { } card && ViewModel.RemoveCommand.CanExecute(card))
        {
            ViewModel.RemoveCommand.Execute(card);
        }
        _menuCard = null;
    }

    private static Button? CardButton(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is Button { DataContext: MediaCard } button)
            {
                return button;
            }
        }
        return null;
    }

    private static HomeRowViewModel? RowOf(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: HomeRowViewModel row })
            {
                return row;
            }
        }
        return null;
    }
}
