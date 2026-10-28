using System.ComponentModel;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>The hero of <see cref="Views.ItemDetailPage"/>; it shows and acts on <see cref="ViewModel"/>.</summary>
public sealed partial class DetailHero : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ItemDetailViewModel), typeof(DetailHero), new PropertyMetadata(null, OnViewModelChanged));

    private const int CollapsedOverviewLines = 3;

    public DetailHero()
    {
        InitializeComponent();
    }

    public ItemDetailViewModel? ViewModel
    {
        get => (ItemDetailViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Static and instance helpers because x:Bind function bindings call them.
    public static ImageSource? LoadImage(Uri? url, int decodeWidth) => Artwork.Load(url, decodeWidth);

    public static Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static int OverviewLines(bool isExpanded) => isExpanded ? 0 : CollapsedOverviewLines;

    public static Style ChipStyle(bool isAccent) => (Style)Application.Current.Resources[isAccent ? "SiloAccentChipStyle" : "SiloChipStyle"];

    public static Brush ChipForeground(bool isAccent) => Resource(isAccent ? "AccentTextFillColorPrimaryBrush" : "SiloForegroundBrush");

    public static Brush ActiveForeground(bool isActive) => Resource(isActive ? "SiloPrimaryBrush" : "SiloForegroundBrush");

    // Segoe Fluent Icons: Heart, HeartFill; Add, Accept.
    public static string HeartGlyph(bool isFavorite) => isFavorite ? "" : "";

    public static string WatchlistGlyph(bool inWatchlist) => inWatchlist ? "" : "";

    public string OverviewToggleName(bool isExpanded) => isExpanded ? "Show less of the overview" : "Show more of the overview";

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var hero = (DetailHero)sender;
        if (e.OldValue is ItemDetailViewModel old)
        {
            old.PropertyChanged -= hero.OnViewModelPropertyChanged;
        }
        if (e.NewValue is ItemDetailViewModel current)
        {
            current.PropertyChanged += hero.OnViewModelPropertyChanged;
        }
        hero.ShowCredits();
        hero.UpdateOverviewToggle();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ItemDetailViewModel.CreditParts):
                ShowCredits();
                break;
            case nameof(ItemDetailViewModel.IsOverviewExpanded) or nameof(ItemDetailViewModel.Overview):
                UpdateOverviewToggle();
                break;
        }
    }

    // "Directed by" muted, the names brighter, parts and genres separated by dots, as on the web.
    private void ShowCredits()
    {
        CreditsText.Inlines.Clear();
        var parts = ViewModel?.CreditParts ?? [];
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                CreditsText.Inlines.Add(new Run { Text = "  ·  ", Foreground = LocalBrush("DetailCreditSeparatorBrush") });
            }
            if (parts[i].Label is { } label)
            {
                CreditsText.Inlines.Add(new Run { Text = label + " ", Foreground = LocalBrush("DetailCreditLabelBrush") });
            }
            CreditsText.Inlines.Add(new Run
            {
                Text = parts[i].Text,
                Foreground = LocalBrush("DetailCreditNameBrush"),
                FontFamily = (FontFamily)Application.Current.Resources["SiloFontMedium"],
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            });
        }
    }

    private Brush LocalBrush(string key) => (Brush)Resources[key];

    // "More" only when the clamp hides text; "Less" while expanded.
    private void UpdateOverviewToggle() =>
        OverviewToggle.Visibility = ViewModel is { HasOverview: true } viewModel && (viewModel.IsOverviewExpanded || OverviewText.IsTextTrimmed)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnOverviewTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateOverviewToggle();

    private void OnBackdropOpened(object sender, RoutedEventArgs e) => Backdrop.Opacity = 1;

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
            await collections.LoadAsync(item.ContentId, item.DisplayTitle);
            await shown;
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed read or tick is the dialog's to say; this is anything else.
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogError(ex, "Add to collection could not be shown");
        }
    }
}
