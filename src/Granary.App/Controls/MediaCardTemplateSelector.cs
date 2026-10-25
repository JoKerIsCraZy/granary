using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>A card's template, or the skeleton's for <see cref="MediaCard.Placeholder"/>.</summary>
public sealed partial class MediaCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Card { get; set; }

    public DataTemplate? Skeleton { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is MediaCard { IsPlaceholder: true } ? Skeleton : Card;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
