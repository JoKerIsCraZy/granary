using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

public sealed partial class DetailEpisodeCard : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(nameof(Episode), typeof(DetailEpisode), typeof(DetailEpisodeCard), new PropertyMetadata(null));

    public DetailEpisodeCard()
    {
        InitializeComponent();
    }

    public DetailEpisode? Episode
    {
        get => (DetailEpisode?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    // Instance method because x:Bind function bindings call it on the control.
    public Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
