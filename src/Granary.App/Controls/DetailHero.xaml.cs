using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>The hero of <see cref="Views.ItemDetailPage"/>; it shows and acts on <see cref="ViewModel"/>.</summary>
public sealed partial class DetailHero : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ItemDetailViewModel), typeof(DetailHero), new PropertyMetadata(null));

    // The Info dialog: wide, most of the window's height.
    private const double InfoWidth = 980;
    private const double InfoHeightShare = 0.86;
    private const double InfoChrome = 170;

    public DetailHero()
    {
        InitializeComponent();
    }

    public ItemDetailViewModel? ViewModel
    {
        get => (ItemDetailViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Static helpers because x:Bind function bindings call them; TitlePage shares them.
    public static ImageSource? LoadImage(Uri? url, int decodeWidth) => Artwork.Load(url, decodeWidth);

    public static Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Style ChipStyle(bool isAccent) => (Style)Application.Current.Resources[isAccent ? "SiloAccentChipStyle" : "SiloChipStyle"];

    public static Brush ChipForeground(bool isAccent) => Resource(isAccent ? "AccentTextFillColorPrimaryBrush" : "SiloForegroundBrush");

    public static Brush ActiveForeground(bool isActive) => Resource(isActive ? "SiloPrimaryBrush" : "SiloForegroundBrush");

    // Segoe Fluent Icons: Heart, HeartFill; Add, Accept.
    public static string HeartGlyph(bool isFavorite) => isFavorite ? "" : "";

    public static string WatchlistGlyph(bool inWatchlist) => inWatchlist ? "" : "";

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    // A missing or expired logo: the display title takes its place.
    private void OnLogoFailed(object sender, ExceptionRoutedEventArgs e) => ViewModel?.OnLogoFailed();

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailCrumb crumb } && ViewModel?.OpenCrumbCommand is { } command && command.CanExecute(crumb))
        {
            command.Execute(crumb);
        }
    }

    private void OnActionErrorClosed(InfoBar sender, object args) => ViewModel?.DismissActionError();

    // A picker's menu takes its rows as it opens and keeps them while it is open: the page reads the item again after a
    // save, and new rows under an open menu would take the focus out of it. It opens on the chosen row, so the arrows
    // start from there.
    private void OnVersionMenuOpening(object? sender, object e) => VersionRows.ItemsSource = ViewModel?.Pickers.Versions;

    private void OnAudioMenuOpening(object? sender, object e) => AudioRows.ItemsSource = ViewModel?.Pickers.Audio;

    private void OnSubtitleMenuOpening(object? sender, object e) => SubtitleRows.ItemsSource = ViewModel?.Pickers.Subtitles;

    private void OnVersionMenuOpened(object? sender, object e) =>
        FocusRow(VersionRows, (VersionRows.ItemsSource as IReadOnlyList<DetailVersionOption>)?.ToList().FindIndex(option => option.IsSelected) ?? -1);

    private void OnAudioMenuOpened(object? sender, object e) =>
        FocusRow(AudioRows, (AudioRows.ItemsSource as IReadOnlyList<DetailAudioOption>)?.ToList().FindIndex(option => option.IsSelected) ?? -1);

    private void OnSubtitleMenuOpened(object? sender, object e) =>
        FocusRow(SubtitleRows, (SubtitleRows.ItemsSource as IReadOnlyList<DetailSubtitleOption>)?.ToList().FindIndex(option => option.IsSelected) ?? -1);

    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailVersionOption option })
        {
            VersionMenu.Hide();
            ViewModel?.SelectVersionCommand.Execute(option);
        }
    }

    private void OnAudioClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailAudioOption option })
        {
            AudioMenu.Hide();
            ViewModel?.SelectAudioCommand.Execute(option);
        }
    }

    private void OnSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailSubtitleOption option })
        {
            SubtitleMenu.Hide();
            ViewModel?.SelectSubtitleCommand.Execute(option);
        }
    }

    private static void FocusRow(ItemsControl rows, int index)
    {
        // The rows were handed over as the menu opened: lay them out before looking for one.
        rows.UpdateLayout();
        if (index >= 0 && rows.ContainerFromIndex(index) is DependencyObject row && FirstButton(row) is { } button)
        {
            button.Focus(FocusState.Programmatic);
        }
    }

    // The row's button: the template's root, or under a subtitle section's heading.
    private static Button? FirstButton(DependencyObject element)
    {
        if (element is Button button)
        {
            return button;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FirstButton(VisualTreeHelper.GetChild(element, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    // Everything the hero leaves out: the full overview, ratings, cast and crew by job, and each file's facts.
    private async void OnInfoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } item || XamlRoot is not { } root)
        {
            return;
        }
        var panel = new DetailInfoPanel(item.InfoSections);
        // A resized window resizes the open dialog's list.
        void Fit(XamlRoot sender, XamlRootChangedEventArgs args) => panel.MaxHeight = PanelHeight(sender);
        try
        {
            panel.MaxHeight = PanelHeight(root);
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = item.DisplayTitle,
                Content = panel,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = InfoWidth;
            dialog.Resources["ContentDialogMaxHeight"] = root.Size.Height * InfoHeightShare;
            root.Changed += Fit;
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape (another dialog may already be open).
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogWarning("The Info dialog could not be shown: {Error}", ex.GetType().Name);
        }
        finally
        {
            root.Changed -= Fit;
        }
    }

    private static double PanelHeight(XamlRoot root) => Math.Max(240, root.Size.Height * InfoHeightShare - InfoChrome);

    // The web's "Add to a collection": the profile's own manual collections, ticked where the title is in them.
    private async void OnAddToCollectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { ContentId.Length: > 0 } item)
        {
            return;
        }
        try
        {
            var collections = App.Services.GetRequiredService<AddToCollectionViewModel>();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Add to a collection",
                Content = new AddToCollectionPanel(collections),
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
            };
            var shown = dialog.ShowAsync();
            try
            {
                await collections.LoadAsync(item.ContentId, item.DisplayTitle);
            }
            catch
            {
                // Not left open, empty and loading.
                dialog.Hide();
                throw;
            }
            await shown;
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed read or tick is the dialog's to say; this is anything else.
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogError(ex, "Add to collection could not be shown");
        }
    }
}
