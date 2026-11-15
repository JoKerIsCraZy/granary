using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>A card's template in a row, or the place's for an episode the library does not have (an episode's More Episodes).</summary>
public sealed partial class MediaRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Card { get; set; }

    public DataTemplate? Placeholder { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is DetailEpisodePlaceholder ? Placeholder : Card;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
