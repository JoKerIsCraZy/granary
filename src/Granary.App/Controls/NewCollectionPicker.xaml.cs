using Granary.Core.Api.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>The New collection picker's content: <see cref="Picked"/> names the kind chosen.</summary>
public sealed partial class NewCollectionPicker : UserControl
{
    public NewCollectionPicker()
    {
        InitializeComponent();
    }

    /// <summary>Raised with <see cref="PersonalCollectionTypes.Manual"/> or <see cref="PersonalCollectionTypes.Smart"/>.</summary>
    public event EventHandler<string>? Picked;

    private void OnManualClick(object sender, RoutedEventArgs e) => Picked?.Invoke(this, PersonalCollectionTypes.Manual);

    private void OnSmartClick(object sender, RoutedEventArgs e) => Picked?.Invoke(this, PersonalCollectionTypes.Smart);
}
