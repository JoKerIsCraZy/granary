using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>An episode's card template in a season's grid, or the place's for an episode the library does not have.</summary>
public sealed partial class DetailEpisodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Episode { get; set; }

    public DataTemplate? Placeholder { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is DetailEpisodePlaceholder ? Placeholder : Episode;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
