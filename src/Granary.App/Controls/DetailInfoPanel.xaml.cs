using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>What a title's Info dialog lists (<see cref="DetailFacts.InfoSections"/>).</summary>
public sealed partial class DetailInfoPanel : UserControl
{
    public DetailInfoPanel(IReadOnlyList<DetailInfoSection> sections)
    {
        Sections = sections;
        InitializeComponent();
    }

    public IReadOnlyList<DetailInfoSection> Sections { get; }
}
