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

    /// <summary>
    /// One pill-shaped track holding the choices as segments, the chosen one filled light inside it (the Calendar's presets,
    /// the user's chosen design); set before the items. The track is <see cref="SegmentedHeight"/> high.
    /// </summary>
    public bool IsSegmented { get; set; }

    /// <summary>A segmented control's height, for a control beside it to match.</summary>
    public const double SegmentedHeight = 40;

    private void Build()
    {
        ShowTrack();
        Pills.Children.Clear();
        foreach (var choice in ItemsSource ?? [])
        {
            var pill = new RadioButton
            {
                Content = choice.Label,
                Tag = choice,
                GroupName = _group,
                Style = (Style)Resources[IsSegmented ? "SegmentStyle" : "PillStyle"],
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

    // The mockup's track: #161619 in a 1 px ring of #242428, the segments 4 px in from its edge (the ring and 3 px).
    private void ShowTrack()
    {
        if (!IsSegmented)
        {
            return;
        }
        Track.Height = SegmentedHeight;
        Track.Padding = new Thickness(3);
        Track.BorderThickness = new Thickness(1);
        Track.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x16, 0x19));
        Track.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x24, 0x24, 0x28));
        Pill.SetIsEnabled(Track, true);
        Pills.Spacing = 2;
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
