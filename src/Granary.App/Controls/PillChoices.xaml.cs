using Granary.Core.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>The chosen pill in the accent red with white text, as the web's calendar presets; set before the items.</summary>
    public bool IsAccent { get; set; }

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
            if (IsAccent)
            {
                pill.Resources["PillChosenBackgroundBrush"] = Application.Current.Resources["SiloPrimaryBrush"];
                pill.Resources["PillChosenForegroundBrush"] = new SolidColorBrush(Colors.White);
            }
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
