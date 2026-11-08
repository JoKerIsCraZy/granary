using System.Numerics;
using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Granary.App.Controls;

public sealed partial class DetailExtraCard : UserControl
{
    public static readonly DependencyProperty ExtraProperty =
        DependencyProperty.Register(nameof(Extra), typeof(DetailExtra), typeof(DetailExtraCard), new PropertyMetadata(null, OnExtraChanged));

    // As wide as a card gets: a card alone in its row stays under two of the grid's 280 px cards and their 20 px gap, where
    // a second column fits, and more columns are narrower. In logical pixels, so the display's scale comes on top.
    private const int StillDecodeWidth = 580;

    // The web's: the still grows 5 % under the pointer, over 300 ms.
    private const float HoverScale = 1.05f;
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(300);

    // Cancelled when the container is recycled for another extra, so the old still stays out.
    private CancellationTokenSource? _still;
    private double _width;

    public DetailExtraCard()
    {
        InitializeComponent();
        StillImage.ScaleTransition = new Vector3Transition { Duration = ZoomDuration };
    }

    public DetailExtra? Extra
    {
        get => (DetailExtra?)GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    private static void OnExtraChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((DetailExtraCard)sender).Show((DetailExtra?)e.NewValue);

    // Every part is set for every extra, here rather than by x:Bind functions: those are skipped when their argument is
    // null, and containers are recycled.
    private void Show(DetailExtra? extra)
    {
        _still?.Cancel();
        _still = null;
        BlurImage.Source = Thumbhashes.Get(extra?.StillThumbhash);
        StillImage.Source = null;
        if (extra?.StillUrl is { } url)
        {
            _still = new CancellationTokenSource();
            StillImage.Source = Artwork.Load(url, StillDecodeWidth, _still.Token);
        }
        PlayMark.Visibility = StillImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = extra?.Title ?? "";
        DurationText.Text = extra?.Duration ?? "";
        DurationText.Visibility = string.IsNullOrEmpty(extra?.Duration) ? Visibility.Collapsed : Visibility.Visible;
        SetHovered(false);
    }

    // A still that fails to load (an expired address, a deleted file) leaves the thumbhash with the play mark, as the web's
    // falls back to its placeholder rather than an empty frame.
    private void OnStillFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StillImage.Source = null;
        PlayMark.Visibility = Visibility.Visible;
    }

    // The picture's height follows the width the card is measured at, as MediaCardView sizes it: from the measure, never
    // after arrange, or the grid's rows would not settle.
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        if (double.IsFinite(width) && width > 0 && width != _width)
        {
            _width = width;
            double height = width * 9 / 16;
            Art.Height = height;
            StillImage.CenterPoint = new Vector3((float)(width / 2), (float)(height / 2), 0);
        }
        return base.MeasureOverride(availableSize);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetHovered(Extra is not null);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHovered(false);

    private void SetHovered(bool hovered)
    {
        HoverLayer.Opacity = hovered ? 1 : 0;
        StillImage.Scale = hovered ? new Vector3(HoverScale, HoverScale, 1) : Vector3.One;
    }
}
