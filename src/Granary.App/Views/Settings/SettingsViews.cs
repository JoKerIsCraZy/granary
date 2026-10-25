using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Settings;

/// <summary>Settings → Playback; it reads the profile's values when it is shown.</summary>
public sealed partial class PlaybackSettingsView : UserControl
{
    public PlaybackSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<PlaybackSettingsViewModel>(App.Services);
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public PlaybackSettingsViewModel ViewModel { get; }
}

/// <summary>Settings → Language &amp; Subtitles; it reads the profile's values when it is shown.</summary>
public sealed partial class LanguageSettingsView : UserControl
{
    public LanguageSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<LanguageSettingsViewModel>(App.Services);
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public LanguageSettingsViewModel ViewModel { get; }

    // The palette is the one of the list the swatch is in (its Tag).
    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex } swatch)
        {
            return;
        }
        DependencyObject? parent = swatch;
        while (parent is not null and not ItemsControl)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        switch ((parent as ItemsControl)?.Tag as string)
        {
            case "font":
                ViewModel.PickFontColor(hex);
                break;
            case "outline":
                ViewModel.PickOutlineColor(hex);
                break;
            case "background":
                ViewModel.PickBackgroundColor(hex);
                break;
        }
    }
}
