using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>The New collection picker's content: <see cref="Picked"/> names the kind chosen.</summary>
public sealed partial class NewCollectionPicker : UserControl
{
    public NewCollectionPicker()
    {
        InitializeComponent();
        // "Good for" and its examples are runs of one wrapping line, which x:Bind functions can't fill.
        string goodFor = AppText.Get("web.collections.newPicker.goodFor");
        ManualGoodForLabel.Text = goodFor;
        SmartGoodForLabel.Text = goodFor;
        ManualGoodFor.Text = AppText.Get("web.collections.copy.goodForManualPersonal");
        SmartGoodFor.Text = AppText.Get("web.collections.copy.goodForSmart");
        AutomationProperties.SetName(ManualCard, AppText.Get("granary.collections.newPicker.kindName",
            ("kind", AppText.Get("web.collections.types.kindManual")), ("sentence", AppText.Get("web.collections.copy.kindSentenceManual"))));
        AutomationProperties.SetName(SmartCard, AppText.Get("granary.collections.newPicker.kindName",
            ("kind", AppText.Get("web.collections.types.kindSmart")), ("sentence", AppText.Get("web.collections.copy.kindSentenceSmart"))));
    }

    /// <summary>Raised with <see cref="PersonalCollectionTypes.Manual"/> or <see cref="PersonalCollectionTypes.Smart"/>.</summary>
    public event EventHandler<string>? Picked;

    private void OnManualClick(object sender, RoutedEventArgs e) => Picked?.Invoke(this, PersonalCollectionTypes.Manual);

    private void OnSmartClick(object sender, RoutedEventArgs e) => Picked?.Invoke(this, PersonalCollectionTypes.Smart);
}
