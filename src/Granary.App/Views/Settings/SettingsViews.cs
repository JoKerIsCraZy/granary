using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;
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
        // Once: the page keeps the view, and coming back to it must not overwrite what is shown.
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(ViewModel.LoadAsync);
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
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(ViewModel.LoadAsync);
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
                ViewModel.Look.PickFontColor(hex);
                break;
            case "outline":
                ViewModel.Look.PickOutlineColor(hex);
                break;
            case "background":
                ViewModel.Look.PickBackgroundColor(hex);
                break;
        }
    }
}

/// <summary>A settings view's first read: the page says what went wrong itself, and nothing escapes the event handler.</summary>
internal static class SettingsViewLoad
{
    public static async Task RunAsync(Func<CancellationToken, Task> load)
    {
        try
        {
            await load(CancellationToken.None);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SettingsViewLoad)).LogError(ex, "A settings page could not load");
        }
    }
}
