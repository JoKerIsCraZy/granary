using System.ComponentModel;
using Granary.App.Services;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// A trailer over the window (<see cref="TrailerOverlayViewModel"/>), in YouTube's embedded player: the frame, its title
/// and close button, the way out (Esc, Close, Back, the scrim), full screen, and "Open on YouTube" when it can't play here.
/// The WebView2 itself is in TrailerOverlay.Browser.cs.
/// </summary>
public sealed partial class TrailerOverlay : UserControl
{
    // The dialog's top row, with the title and the close button, and its 1 px border.
    private const double HeaderHeight = 64;
    private const double DialogBorder = 1;

    // The web's dialog keeps 1rem from the window's edges; MainWindow's title bar is 32 px high.
    private const double Inset = 16;
    private const double TitleBarHeight = 32;

    private readonly ILogger<TrailerOverlay> _logger;
    private readonly WindowedPlacement _windowed = new();
    private bool _isFullScreen;
    private FrameworkElement? _returnFocus;

    public TrailerOverlay()
    {
        ViewModel = App.Services.GetRequiredService<TrailerOverlayViewModel>();
        _logger = App.Services.GetRequiredService<ILogger<TrailerOverlay>>();
        InitializeComponent();
        FallbackText.Text = TrailerOverlayViewModel.UnavailableText;
        ViewModel.Opened += OnOpened;
        ViewModel.PropertyChanged += OnViewModelChanged;
        SizeChanged += (_, _) => Fit();
        // The mouse's back button closes the trailer before the window would go back; the WebView marks its own handled.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
    }

    public TrailerOverlayViewModel ViewModel { get; }

    /// <summary>
    /// The trailer fills the window, or no longer does. The window hides the app under it meanwhile, so the page under the
    /// trailer is not laid out at the screen's size and keeps its place.
    /// </summary>
    public event EventHandler<bool>? FullScreenChanged;

    private void OnOpened(object? sender, EventArgs e)
    {
        TearDown();
        _returnFocus ??= XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as FrameworkElement : null;
        var web = new WebView2 { DefaultBackgroundColor = Colors.Black, Opacity = 0 };
        AutomationProperties.SetName(web, "Trailer");
        _web = web;
        WebHost.Child = web;
        Fit();
        CloseButton.Focus(FocusState.Programmatic);
        StartBrowser(web);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrailerOverlayViewModel.State))
        {
            return;
        }
        switch (ViewModel.State)
        {
            // Shown once the player answered, so neither a blank page nor YouTube's set-up flashes up.
            case TrailerOverlayState.Playing when _web is { } web:
                web.Opacity = 1;
                break;
            case TrailerOverlayState.Unavailable:
                SetFullScreen(false);
                TearDown();
                OpenOnYouTubeButton.Focus(FocusState.Programmatic);
                break;
            case TrailerOverlayState.Closed:
                SetFullScreen(false);
                TearDown();
                ReturnFocus();
                break;
        }
    }

    // Back where the viewer was: the trailer's card, if its page is still there.
    private void ReturnFocus()
    {
        if (_returnFocus is { IsLoaded: true } element)
        {
            element.Focus(FocusState.Programmatic);
        }
        _returnFocus = null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => ViewModel.Close();

    // As before trailers played here: YouTube's page in the browser.
    private async void OnOpenOnYouTubeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WatchUrl is not { } watch)
        {
            return;
        }
        try
        {
            if (await Launcher.LaunchUriAsync(watch))
            {
                ViewModel.Close();
            }
            else
            {
                _logger.LogWarning("The browser did not open the trailer");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The trailer could not be opened: {Error}", ex.GetType().Name);
        }
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Scrim))
        {
            e.Handled = true;
            ViewModel.Close();
        }
    }

    // In full screen Esc returns to the window first, as the player's does.
    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_isFullScreen)
        {
            LeavePageFullScreen();
            SetFullScreen(false);
            return;
        }
        ViewModel.Close();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsOpen && e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed)
        {
            e.Handled = true;
            ViewModel.Close();
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen)
        {
            return;
        }
        _isFullScreen = fullScreen;
        var window = XamlRoot?.ContentIslandEnvironment is { } island ? AppWindow.GetFromWindowId(island.AppWindowId) : null;
        if (fullScreen)
        {
            FullScreenChanged?.Invoke(this, true);
            if (window is not null)
            {
                _windowed.Keep(window);
                window.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
        }
        else
        {
            if (window is not null)
            {
                _windowed.Restore(window);
            }
            FullScreenChanged?.Invoke(this, false);
        }
        // Full screen is the frame alone, edge to edge.
        Header.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Dialog.CornerRadius = fullScreen ? default : (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
        Dialog.BorderThickness = fullScreen ? default : new Thickness(DialogBorder);
        Scrim.Background = fullScreen ? new SolidColorBrush(Colors.Black) : (Brush)Resources["TrailerScrimBrush"];
        Fit();
    }

    // The 16:9 frame as large as the room allows (TrailerFrame), or the whole window in full screen.
    private void Fit()
    {
        if (_isFullScreen)
        {
            Dialog.Margin = default;
            Frame.Width = ActualWidth;
            Frame.Height = ActualHeight;
            return;
        }
        double chrome = 2 * (Inset + DialogBorder);
        var (width, height) = TrailerFrame.Fit(ActualWidth - chrome, ActualHeight - TitleBarHeight - chrome - HeaderHeight);
        Dialog.Margin = new Thickness(Inset, TitleBarHeight + Inset, Inset, Inset);
        Frame.Width = width;
        Frame.Height = height;
    }
}
