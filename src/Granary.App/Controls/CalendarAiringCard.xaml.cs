using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

public sealed partial class CalendarAiringCard : UserControl
{
    public static readonly DependencyProperty AiringProperty =
        DependencyProperty.Register(nameof(Airing), typeof(CalendarAiring), typeof(CalendarAiringCard), new PropertyMetadata(null, OnAiringChanged));

    // A width change of more than this part decodes the poster again (a window resized past a few pixels).
    private const double RedecodeChange = 0.1;

    private static readonly Brush Shade = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x0E, 0x0E, 0x10));
    private static readonly Brush PlainRing = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xE8, 0xE8, 0xEC));
    private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    // Cancelled when the container is recycled for another airing, so the old poster stays out, and when the card leaves the
    // tree before its poster is in. Null while no poster is loading or in.
    private CancellationTokenSource? _artwork;
    private double _width;
    private double _decodedWidth;
    private bool _loadQueued;

    public CalendarAiringCard()
    {
        InitializeComponent();
    }

    public CalendarAiring? Airing
    {
        get => (CalendarAiring?)GetValue(AiringProperty);
        set => SetValue(AiringProperty, value);
    }

    private static void OnAiringChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((CalendarAiringCard)sender).Show((CalendarAiring?)e.NewValue);

    // Every part is set for every airing, here rather than by x:Bind functions: containers are recycled.
    private void Show(CalendarAiring? airing)
    {
        StopArtwork();
        ArtImage.Source = null;
        _decodedWidth = 0;
        BlurImage.Source = Thumbhashes.Get(airing?.ImageThumbhash);
        TitleText.Text = airing?.Title ?? "";
        CodeText.Text = airing?.Code ?? "";
        CodeText.Visibility = Visible(airing?.HasCode == true);
        TimeText.Text = airing?.Time ?? "";
        TimeChip.Visibility = Visible(airing?.HasTime == true);
        WatchedMark.Visibility = Visible(airing?.IsWatched == true);
        ShowBadge(airing);
        QueueArtwork();
    }

    // As the mockup the user chose: PREMIERE in the accent with its label colour, FINALE outlined in the accent, the others
    // outlined in the text colour, both over a dark shade. The brushes carry the server's accent (AccentPainter).
    private void ShowBadge(CalendarAiring? airing)
    {
        BadgeBox.Visibility = Visible(airing?.HasBadge == true);
        BadgeText.Text = airing?.Badge ?? "";
        var kind = airing?.BadgeKind ?? CalendarBadgeKind.None;
        BadgeBox.Background = kind == CalendarBadgeKind.Premiere ? Resource("SiloPrimaryBrush") : Shade;
        BadgeBox.BorderBrush = kind switch
        {
            CalendarBadgeKind.Premiere => Transparent,
            CalendarBadgeKind.Finale => Resource("SiloPrimaryBrush"),
            _ => PlainRing,
        };
        BadgeText.Foreground = kind switch
        {
            CalendarBadgeKind.Premiere => Resource("SiloOnPrimaryBrush"),
            CalendarBadgeKind.Finale => Resource("SiloPrimaryBrush"),
            _ => Resource("SiloForegroundBrush"),
        };
    }

    // The still's height follows the width the card is measured at, as MediaCardView sizes it: from the measure, never after
    // arrange, or a column's repeater would not settle (AGENTS.md, "Items of an ItemsRepeater").
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        if (double.IsFinite(width) && width > 0 && width != _width)
        {
            _width = width;
            Still.Height = width * 9 / 16;
            QueueArtwork();
        }
        return base.MeasureOverride(availableSize);
    }

    // Loaded once the card has its width, after the layout pass: a source set while measuring would start another one.
    private void QueueArtwork()
    {
        if (_loadQueued || _width <= 0 || Airing?.ImageUrl is null)
        {
            return;
        }
        if (_decodedWidth > 0 && Math.Abs(_width - _decodedWidth) <= _decodedWidth * RedecodeChange)
        {
            return;
        }
        _loadQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _loadQueued = false;
            LoadArtwork();
        });
    }

    // The poster decoded as wide as the card shows it: its middle fills the 16:9 still.
    private void LoadArtwork()
    {
        if (Airing?.ImageUrl is not { } url || _width <= 0 || !IsLoaded)
        {
            return;
        }
        StopArtwork();
        _artwork = new CancellationTokenSource();
        _decodedWidth = _width;
        ArtImage.Source = Artwork.Load(url, (int)Math.Ceiling(_width), _artwork.Token);
    }

    private void StopArtwork()
    {
        _artwork?.Cancel();
        _artwork = null;
    }

    // An expired or broken poster leaves the blur, or the plain still, in place.
    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => ArtImage.Source = null;

    // Back in the tree with its poster stopped on the way: it loads again.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ArtImage.Source is null)
        {
            _decodedWidth = 0;
            QueueArtwork();
        }
    }

    // A poster on its way when the card leaves the tree (the page left) stops loading.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // WinUI can raise Unloaded while the card is still in the tree (DetailExtraCard): such an Unloaded changes nothing.
        if (IsLoaded)
        {
            return;
        }
        if (_artwork is not null)
        {
            StopArtwork();
            ArtImage.Source = null;
        }
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
