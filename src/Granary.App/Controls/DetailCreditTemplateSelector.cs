using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>A "Cast &amp; Crew" entry's template: a person's card, or the separator before a group.</summary>
public sealed partial class DetailCreditTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Person { get; set; }

    public DataTemplate? Separator { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is DetailCreditSeparator ? Separator : Person;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
