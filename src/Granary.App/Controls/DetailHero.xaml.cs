using System.ComponentModel;
using Granary.App.Services;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>The hero of <see cref="Views.ItemDetailPage"/>; it shows and acts on <see cref="ViewModel"/>.</summary>
public sealed partial class DetailHero : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ItemDetailViewModel), typeof(DetailHero), new PropertyMetadata(null, OnViewModelChanged));

    // The Info dialog: wide, most of the window's height.
    private const double InfoWidth = 980;
    private const double InfoHeightShare = 0.86;
    private const double InfoChrome = 170;

    public DetailHero()
    {
        Party = App.Services.GetRequiredService<StartPartyViewModel>();
        Party.CopyText = RoomClipboard.Copy;
        InitializeComponent();
        // The menu's Watch Party entry follows the page, the server's rooms and this profile's room while the hero shows.
        // Loaded and Unloaded can come out of order (an Unloaded after the next Loaded), so they are counted: only the
        // last Unloaded lets go, and the session and the server's rooms, which live for the whole app, no longer hold the
        // page.
        Loaded += (_, _) =>
        {
            _loads++;
            if (ViewModel is { } page)
            {
                Party.Attach(page);
            }
        };
        Unloaded += (_, _) =>
        {
            _loads = Math.Max(0, _loads - 1);
            if (_loads == 0)
            {
                Party.Detach();
            }
        };
    }

    private int _loads;

    // A view model set or swapped while the hero shows: the Watch Party entry follows the new one, and none without one.
    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DetailHero { _loads: > 0 } hero)
        {
            return;
        }
        if (e.NewValue is ItemDetailViewModel page)
        {
            hero.Party.Attach(page);
        }
        else
        {
            hero.Party.Detach();
        }
    }

    /// <summary>"Start a Watch Party" in the More menu, and the sheet it opens.</summary>
    public StartPartyViewModel Party { get; }

    /// <summary>Play, where keyboard focus starts when the page opens with a remote, as on Silo's TV app.</summary>
    public Control ArrivalFocus => PlayButton;

    public ItemDetailViewModel? ViewModel
    {
        get => (ItemDetailViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Static helpers because x:Bind function bindings call them; TitlePage shares them.
    public static ImageSource? LoadImage(Uri? url, int decodeWidth) => Artwork.Load(url, decodeWidth);

    public static Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Either(bool first, bool second) => first || second ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Both(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public static Style ChipStyle(bool isAccent) => (Style)Application.Current.Resources[isAccent ? "SiloAccentChipStyle" : "SiloChipStyle"];

    public static Brush ChipForeground(bool isAccent) => Resource(isAccent ? "AccentTextFillColorPrimaryBrush" : "SiloForegroundBrush");

    public static Brush ActiveForeground(bool isActive) => Resource(isActive ? "SiloPrimaryBrush" : "SiloForegroundBrush");

    // A favourite's heart stays red whatever the accent: the web's text-red-400 (ItemDetail/components/ActionBar.tsx).
    public static Brush FavoriteForeground(bool isFavorite) => Resource(isFavorite ? "SiloFavoriteLightBrush" : "SiloForegroundBrush");

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

    // The link goes with what it hid: keyboard focus moves on to Play, the next stop, rather than to nowhere.
    private void OnRevealOverviewClick(object sender, RoutedEventArgs e)
    {
        bool byKeyboard = RevealOverviewButton.FocusState is FocusState.Keyboard or FocusState.Programmatic;
        ViewModel?.RevealOverviewCommand.Execute(null);
        if (byKeyboard)
        {
            PlayButton.Focus(FocusState.Keyboard);
        }
    }

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

    // The web's StartPartySheet, in a dialog: it closes once the room opened (or the profile it was for is gone), or with
    // Cancel, which leaves focus where the menu was.
    private async void OnStartWatchPartyClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not { } root || !Party.Open())
        {
            return;
        }
        var focus = (sender as Control)?.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        StartPartyPanel? panel = null;
        ContentDialog? dialog = null;
        void CloseWhenDone(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(StartPartyViewModel.IsDone) && Party.IsDone)
            {
                dialog?.Hide();
            }
        }
        Party.PropertyChanged += CloseWhenDone;
        try
        {
            panel = new StartPartyPanel(Party);
            dialog = new ContentDialog
            {
                XamlRoot = root,
                Content = panel,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.None,
            };
            AutomationProperties.SetName(dialog, "Start a party with this");
            await dialog.ShowAsync();
            if (!Party.IsDone && IsLoaded)
            {
                MoreButton.Focus(focus);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape (another dialog may already be open).
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogWarning("The Watch Party sheet could not be shown: {Error}", ex.GetType().Name);
        }
        finally
        {
            Party.PropertyChanged -= CloseWhenDone;
            panel?.Detach();
            Party.Close();
        }
    }

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
