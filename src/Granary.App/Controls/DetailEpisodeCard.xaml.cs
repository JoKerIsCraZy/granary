using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

public sealed partial class DetailEpisodeCard : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(nameof(Episode), typeof(DetailEpisode), typeof(DetailEpisodeCard), new PropertyMetadata(null, OnEpisodeChanged));

    public DetailEpisodeCard()
    {
        InitializeComponent();
    }

    public DetailEpisode? Episode
    {
        get => (DetailEpisode?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    // Set here, not by an x:Bind function: those are skipped when their argument is null, and containers are recycled.
    private static void OnEpisodeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((DetailEpisodeCard)sender).OverviewText.Visibility =
            string.IsNullOrEmpty(((DetailEpisode?)e.NewValue)?.Overview) ? Visibility.Collapsed : Visibility.Visible;
}
