using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>What a title's Info dialog lists (<see cref="DetailFacts.InfoSections"/>).</summary>
public sealed partial class DetailInfoPanel : UserControl
{
    // The one row worth copying from; the others are short facts.
    private const string OverviewLabel = "Overview";

    public DetailInfoPanel(IReadOnlyList<DetailInfoSection> sections)
    {
        Sections = sections;
        InitializeComponent();
    }

    public IReadOnlyList<DetailInfoSection> Sections { get; }

    // Static because x:Bind function bindings in the templates call them on the type.
    public static bool Selectable(string label) => label == OverviewLabel;
}
