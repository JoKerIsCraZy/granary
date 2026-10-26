using System.Globalization;
using Granary.Core.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>A row of studios, networks or genres to browse (<see cref="RequestBrandRowViewModel"/>).</summary>
public sealed partial class BrandRow : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(RequestBrandRowViewModel), typeof(BrandRow), new PropertyMetadata(null));

    /// <summary>How far the row reaches past its own edges to the window's (RowBleed); the page's gutter.</summary>
    public static readonly DependencyProperty BleedProperty =
        DependencyProperty.Register(nameof(Bleed), typeof(double), typeof(BrandRow), new PropertyMetadata(RowBleed.PageGutter, (sender, _) => ((BrandRow)sender).ApplyBleed()));

    // A logo is drawn 160 px wide: the tile less its 24 px margins.
    private const int LogoWidth = 160;

    // The web's gray-800 tile and gray-700 ring; a genre's ring is a faint white.
    private static readonly Brush Gray = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x29, 0x37));
    private static readonly Brush GrayRing = new SolidColorBrush(Color.FromArgb(0xFF, 0x37, 0x41, 0x51));
    private static readonly Brush GenreRing = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    private bool _isPointerInside;

    public BrandRow()
    {
        InitializeComponent();
        ApplyBleed();
    }

    public RequestBrandRowViewModel? Row
    {
        get => (RequestBrandRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>How far the row reaches past its own edges to the window's: the gutter of the page it is on.</summary>
    public double Bleed
    {
        get => (double)GetValue(BleedProperty);
        set => SetValue(BleedProperty, value);
    }

    // Static helpers because x:Bind function bindings call them.
    public static Brush TileBackground(bool isGenre, string gradientFrom, string gradientTo)
    {
        if (!isGenre)
        {
            return Gray;
        }
        // The web's 135° gradient: top left to bottom right.
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Parse(gradientFrom), Offset = 0 },
                new GradientStop { Color = Parse(gradientTo), Offset = 1 },
            },
        };
    }

    public static Brush TileRing(bool isGenre) => isGenre ? GenreRing : GrayRing;

    public static BitmapImage? LogoImage(Uri? logo) => Artwork.Load(logo, LogoWidth);

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public string LoadingName(string? title) => $"Loading {title?.ToLowerInvariant()}";

    // "#RRGGBB", which the view model has checked.
    private static Color Parse(string hex)
    {
        uint rgb = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void ApplyBleed() => RowBleed.Apply(Bleed, Track, Tiles, PreviousButton, NextButton);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction) => RowBleed.Page(Scroller, Tiles, Bleed, direction);

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
        UpdateArrows();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        UpdateArrows();
    }

    private void OnViewChanged(ScrollView sender, object args) => UpdateArrows();

    private void UpdateArrows()
    {
        PreviousButton.Visibility = _isPointerInside && Scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _isPointerInside && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
