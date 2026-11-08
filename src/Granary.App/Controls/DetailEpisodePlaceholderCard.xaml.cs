using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

public sealed partial class DetailEpisodePlaceholderCard : UserControl
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(DetailEpisodePlaceholder),
        typeof(DetailEpisodePlaceholderCard), new PropertyMetadata(null, OnPlaceholderChanged));

    public DetailEpisodePlaceholderCard()
    {
        InitializeComponent();
        MissingText.Text = DetailEpisodePlaceholder.MissingBadge;
    }

    public DetailEpisodePlaceholder? Placeholder
    {
        get => (DetailEpisodePlaceholder?)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    // Set here, not by x:Bind functions: those are skipped when their argument is null, and containers are recycled.
    private static void OnPlaceholderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var card = (DetailEpisodePlaceholderCard)sender;
        var place = (DetailEpisodePlaceholder?)e.NewValue;
        card.TitleText.Text = place?.Title ?? "";
        card.DetailText.Text = place?.Detail ?? "";
        card.DetailText.Visibility = string.IsNullOrEmpty(place?.Detail) ? Visibility.Collapsed : Visibility.Visible;
        card.MissingBadge.Visibility = place?.IsMissing == true ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(card, place?.AccessibleName ?? "");
    }

    // As high as an episode card's still at the width it is measured at, as MediaCardView sizes it: from the measure, never
    // after arrange, or the grid's rows would not settle.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            Box.Height = availableSize.Width * 9 / 16;
        }
        return base.MeasureOverride(availableSize);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DetailEpisodePlaceholderCardAutomationPeer(this);

    // Screen readers hear one line, "Episode 5, missing": text, not a button, as there is nothing to press.
    private sealed partial class DetailEpisodePlaceholderCardAutomationPeer(DetailEpisodePlaceholderCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(DetailEpisodePlaceholderCard);

        protected override bool IsControlElementCore() => true;
    }
}
