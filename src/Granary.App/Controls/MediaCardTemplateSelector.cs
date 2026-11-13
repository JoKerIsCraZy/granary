using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>A card's template, the skeleton's for <see cref="MediaCard.Placeholder"/>, or the one that follows a <see cref="LiveCard"/>.</summary>
public sealed partial class MediaCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Card { get; set; }

    public DataTemplate? Skeleton { get; set; }

    public DataTemplate? Live { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MediaCard { IsPlaceholder: true } => Skeleton,
        LiveCard => Live,
        _ => Card,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
