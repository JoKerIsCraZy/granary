using Granary.Core.ViewModels.Downloads;
using Granary.Core.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>The download dialog's choices for <see cref="ViewModel"/>, made per dialog.</summary>
public sealed partial class DownloadOptionsPanel : UserControl
{
    public DownloadOptionsPanel(DownloadOptionsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        // The dialog's qualities are set before it opens and stay while it shows.
        Qualities = [.. viewModel.Qualities.Select(q => new SettingChoice(q.Preset, q.Label))];
        InitializeComponent();
    }

    public DownloadOptionsViewModel ViewModel { get; }

    public string Description => DownloadOptionsViewModel.Description;

    public string Footer => DownloadOptionsViewModel.Footer;

    /// <summary>The server's qualities as the pills take them.</summary>
    public IReadOnlyList<SettingChoice> Qualities { get; }

    /// <summary>The pill of the quality chosen; a pill pressed chooses its quality.</summary>
    public SettingChoice? SelectedQuality
    {
        get => Qualities.FirstOrDefault(q => q.Value == ViewModel.SelectedQuality?.Preset);
        set
        {
            if (value is not null && ViewModel.Qualities.FirstOrDefault(q => q.Preset == value.Value) is { } quality)
            {
                ViewModel.SelectedQuality = quality;
            }
        }
    }

    // Static because x:Bind function bindings in the template call them on the type.
    public static Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The free space line, red when the download does not fit.</summary>
    public static Brush SpaceForeground(bool isShortOfSpace) =>
        (Brush)Application.Current.Resources[isShortOfSpace ? "SiloDestructiveBrush" : "SiloMutedBrush"];

    // A screen reader says a version as its row shows it: "2160p · HEVC · HDR10, 38.9 GB".
    private void OnVersionContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is DownloadVersionRow row)
        {
            AutomationProperties.SetName(args.ItemContainer, row.SizeText.Length > 0 ? $"{row.Summary}, {row.SizeText}" : row.Summary);
        }
    }
}
