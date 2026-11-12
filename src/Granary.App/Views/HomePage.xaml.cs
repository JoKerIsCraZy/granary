using System.ComponentModel;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;

namespace Granary.App.Views;

public sealed partial class HomePage : Page, IStartupContent
{
    // The pointer on the banner's own room: the hero's strip above the rows' scroller, or the buttons' room in it.
    private bool _pointerOnStrip;
    private bool _pointerOnButtons;
    private bool _isEngaged;
    private bool _isShown;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        Loaded += (_, _) => Hero.FollowScroll(Scroller);
        Scroller.ViewChanged += (_, _) => FollowScroll();
        // The hero's backdrop reaches down behind the first row: only its strip above the scroller is the banner's.
        Hero.PointerEntered += OnPointerOverHero;
        Hero.PointerMoved += OnPointerOverHero;
        Hero.PointerExited += (_, _) => PointerOnStrip(false);
        BannerControls.PointerEntered += (_, _) => PointerOnButtons(true);
        BannerControls.PointerExited += (_, _) => PointerOnButtons(false);
        BannerControls.GotFocus += OnBannerFocused;
        BannerControls.LostFocus += (_, _) => FollowEngagementSoon();
        ViewModel.Banner.PropertyChanged += OnBannerChanged;
    }

    public HomeViewModel ViewModel { get; }

    /// <summary>The latest visit's load of the rows: the start screen waits for the first.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>A remote's Play with focus on the banner's buttons plays its slide, as on a card.</summary>
    public bool PlayBannerFromKey(DependencyObject focused) => BannerControls.PlayFromKey(focused);

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // The page is cached: Back keeps the scroll position, a new visit starts at the top.
        if (e.NavigationMode != NavigationMode.Back)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        // The frame's root: the page's own is not there yet on its first visit.
        var root = Frame?.XamlRoot ?? XamlRoot;
        ViewModel.DisplayScale = root?.RasterizationScale ?? 1;
        ViewModel.Hero.Window = root is null ? default : (root.Size.Width, root.Size.Height);
        ViewModel.Banner.IsMotionReduced = !new UISettings().AnimationsEnabled;
        _isShown = true;
        Hero.Resume();
        try
        {
            var activation = ViewModel.ActivateAsync();
            Ready = activation;
            await activation;
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the view model.
            App.Services.GetRequiredService<ILogger<HomePage>>().LogError(ex, "Home could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Nothing holds the banner of a page that is not shown; its pause button's pause stays for the next visit.
        _isShown = false;
        _pointerOnStrip = _pointerOnButtons = false;
        FollowEngagement();
        ViewModel.Deactivate();
        // The page stays cached: its hero lets go of its pictures until it shows again.
        Hero.Suspend();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Style StatusStyle(bool hasError) =>
        (Style)Application.Current.Resources[hasError ? "SiloBodyTextStyle" : "SiloRowTitleTextStyle"];

    // The banner holds still once the rows fold the hero, as the web's scrolled out of view.
    private void FollowScroll() => ViewModel.Banner.IsInView = !BrowseHeroLayout.IsFolded(Scroller.VerticalOffset, Hero.Sizes);

    private void OnPointerOverHero(object sender, PointerRoutedEventArgs e) =>
        PointerOnStrip(e.GetCurrentPoint(Hero).Position.Y < Hero.Sizes.CompactHeight);

    private void PointerOnStrip(bool isOn)
    {
        if (_pointerOnStrip != isOn)
        {
            _pointerOnStrip = isOn;
            FollowEngagementOrSoon(isOn);
        }
    }

    private void PointerOnButtons(bool isOn)
    {
        _pointerOnButtons = isOn;
        FollowEngagementOrSoon(isOn);
    }

    // A banner that goes (a refresh without it, another profile) takes its buttons with it, and they hear the pointer and
    // focus leave no more.
    private void OnBannerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeaturedBannerViewModel.HasSlides) && !ViewModel.Banner.HasSlides)
        {
            _pointerOnStrip = _pointerOnButtons = false;
            FollowEngagement();
        }
    }

    // Focus that comes with a key shows the whole banner: the rows go back to the top, after the scroll XAML starts for the
    // focus itself.
    private void OnBannerFocused(object sender, RoutedEventArgs e)
    {
        FollowEngagement();
        if (e.OriginalSource is Control { FocusState: FocusState.Keyboard })
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Scroller.ChangeView(null, 0, null));
        }
    }

    // The pointer moving from the hero's strip to the buttons leaves one before it enters the other: letting go is looked
    // at a moment later, so the slideshow does not start a new countdown on the way.
    private void FollowEngagementOrSoon(bool cameOn)
    {
        if (cameOn)
        {
            FollowEngagement();
        }
        else
        {
            FollowEngagementSoon();
        }
    }

    private void FollowEngagementSoon() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FollowEngagement);

    // The pointer on the banner or keyboard focus on its buttons (a remote's arrows, Tab) holds the slideshow and shows its
    // arrows, as the web's hover and focus within; letting go of both starts it again. Focus a click gave a button does not
    // hold it: the slideshow stood still after a click on an arrow, long after the pointer had gone. Only a banner with
    // slides, on a page that is shown, is held.
    private void FollowEngagement()
    {
        bool keyboardOnButtons = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is UIElement { FocusState: FocusState.Keyboard } focused
            && BannerControls.Holds(focused);
        bool engaged = _isShown && ViewModel.Banner.HasSlides && (_pointerOnStrip || _pointerOnButtons || keyboardOnButtons);
        if (engaged == _isEngaged)
        {
            return;
        }
        _isEngaged = engaged;
        BannerControls.IsEngaged = engaged;
        ViewModel.Banner.Hold(engaged);
    }
}
