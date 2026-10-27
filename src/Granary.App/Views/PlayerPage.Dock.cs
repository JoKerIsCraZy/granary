using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// Minimized: the player as the bar at the bottom of the window while the viewer browses (MainWindow.Player.cs). The
// picture plays on in a 160 × 90 box at the bar's left; everything of the full player is hidden, and it neither hides
// the pointer nor keeps the keyboard.
public sealed partial class PlayerPage
{
    public static readonly DependencyProperty IsDockedProperty =
        DependencyProperty.Register(nameof(IsDocked), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    private const double DockVideoWidth = 160;
    private const double DockVideoInset = 8;

    private static readonly SolidColorBrush PlayerBackground = new(Colors.Black);
    private static readonly SolidColorBrush NoBackground = new(Colors.Transparent);

    /// <summary>Minimize asked for the bar.</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>The bar's picture, or its button, asked for the full player again.</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>The bar's Stop asked to end playback.</summary>
    public event EventHandler? StopRequested;

    /// <summary>The player is the bar at the bottom of the window; the window sets it.</summary>
    public bool IsDocked
    {
        get => (bool)GetValue(IsDockedProperty);
        set => SetValue(IsDockedProperty, value);
    }

    /// <summary>The full player's controls are away: in the mini view, or as the bar.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    private void InitializeDock()
    {
        RegisterPropertyChangedCallback(IsDockedProperty, (_, _) => OnDockedChanged());
        RegisterPropertyChangedCallback(IsMiniViewProperty, (_, _) => IsCompact = IsMiniView || IsDocked);
    }

    private void OnDockedChanged()
    {
        IsCompact = IsMiniView || IsDocked;
        Root.Background = IsDocked ? NoBackground : PlayerBackground;
        if (IsDocked)
        {
            // The bar belongs to the window as it is: not full screen, not the mini view.
            SetFullScreen(false);
            SetMiniView(false);
            CloseMenus();
        }
        ShowNextUpScreen();
        ShowControls();
        LiftSubtitles();
        if (!IsDocked)
        {
            Focus(FocusState.Programmatic);
        }
    }

    // The picture's place: in the bar's box, in the Playing Next screen's corner, or the whole page.
    private void PlaceVideoFrame()
    {
        if (IsDocked)
        {
            VideoFrame.Width = DockVideoWidth;
            VideoFrame.Height = DockVideoWidth * 9 / 16;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Left;
            VideoFrame.VerticalAlignment = VerticalAlignment.Center;
            VideoFrame.Margin = new Thickness(DockVideoInset, 0, 0, 0);
            VideoFrame.CornerRadius = new CornerRadius(8);
            VideoFrame.BorderThickness = default;
            Canvas.SetZIndex(VideoFrame, 2);
        }
        else if (ShowsNextUpScreen)
        {
            VideoFrame.Width = MiniPlayerWidth;
            VideoFrame.Height = MiniPlayerWidth * 9 / 16;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Left;
            VideoFrame.VerticalAlignment = VerticalAlignment.Top;
            VideoFrame.Margin = CornerInset(IsFullScreen, 24);
            VideoFrame.CornerRadius = new CornerRadius(16);
            VideoFrame.BorderThickness = new Thickness(1);
            VideoFrame.BorderBrush = MiniPlayerEdge;
            Canvas.SetZIndex(VideoFrame, 2);
        }
        else
        {
            VideoFrame.Width = double.NaN;
            VideoFrame.Height = double.NaN;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoFrame.VerticalAlignment = VerticalAlignment.Stretch;
            VideoFrame.Margin = default;
            VideoFrame.CornerRadius = default;
            VideoFrame.BorderThickness = default;
            Canvas.SetZIndex(VideoFrame, 0);
        }
    }

    // A menu or panel left open would float over the pages.
    private void CloseMenus()
    {
        AudioMenu.Hide();
        ChaptersMenu.Hide();
        SubtitleMenu.Hide();
        SettingsMenu.Hide();
    }

    private bool _isPointerOverDockVideo;

    private void OnDockVideoPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverDockVideo = true;
        ShowDockVideoHover();
    }

    private void OnDockVideoPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverDockVideo = false;
        ShowDockVideoHover();
    }

    private void OnDockVideoFocusChanged(object sender, RoutedEventArgs e) => ShowDockVideoHover();

    private void ShowDockVideoHover() =>
        DockVideoHover.Opacity = _isPointerOverDockVideo || DockVideoButton.FocusState == FocusState.Keyboard ? 1 : 0;

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void OnDockStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility ShowsUndocked(bool isDocked) => isDocked ? Visibility.Collapsed : Visibility.Visible;
}
