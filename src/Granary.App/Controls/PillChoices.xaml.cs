using Granary.Core.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>A pill per choice of <see cref="ItemsSource"/>; <see cref="SelectedItem"/> is the one checked, both ways.</summary>
public sealed partial class PillChoices : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource),
        typeof(IReadOnlyList<SettingChoice>), typeof(PillChoices), new PropertyMetadata(null, (sender, _) => ((PillChoices)sender).Build()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem),
        typeof(SettingChoice), typeof(PillChoices), new PropertyMetadata(null, (sender, _) => ((PillChoices)sender).Check()));

    // Each group of pills is its own radio group.
    private readonly string _group = Guid.NewGuid().ToString("N");
    private bool _checking;

    public PillChoices() => InitializeComponent();

    public IReadOnlyList<SettingChoice>? ItemsSource
    {
        get => (IReadOnlyList<SettingChoice>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public SettingChoice? SelectedItem
    {
        get => (SettingChoice?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    private void Build()
    {
        Pills.Children.Clear();
        foreach (var choice in ItemsSource ?? [])
        {
            var pill = new RadioButton
            {
                Content = choice.Label,
                Tag = choice,
                GroupName = _group,
                Style = (Style)Resources["PillStyle"],
            };
            AutomationProperties.SetName(pill, choice.Label);
            pill.Checked += (_, _) =>
            {
                if (!_checking)
                {
                    SelectedItem = choice;
                }
            };
            Pills.Children.Add(pill);
        }
        Check();
    }

    private void Check()
    {
        _checking = true;
        try
        {
            foreach (var pill in Pills.Children.OfType<RadioButton>())
            {
                pill.IsChecked = Equals(pill.Tag, SelectedItem);
            }
        }
        finally
        {
            _checking = false;
        }
    }
}
