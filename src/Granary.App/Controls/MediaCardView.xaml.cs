using System.Diagnostics;
using System.Numerics;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

public sealed partial class MediaCardView : UserControl
{
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register(nameof(Card), typeof(MediaCard), typeof(MediaCardView), new PropertyMetadata(null, OnCardChanged));

    // Artwork is decoded as large as its box shows it; until the card has been laid out, as a row's cards are.
    private const double PosterHeight = 320;
    private const double LandscapeWidth = 400;

    // Android's: artwork that comes this soon was never waited on and shows at once, later artwork fades in over its blur.
    private static readonly TimeSpan InstantReveal = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(300);

    // Silo's web: the artwork grows 6 % under the pointer, over 300 ms. A poster gets a 40 px disc and 32 px buttons,
    // a wide card 44 and 36; the buttons sit just above a progress bar.
    private const float HoverScale = 1.06f;
    private const double WideCard = 250;
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(300);

    private readonly MediaCardActions _actions = App.Services.GetRequiredService<MediaCardActions>();

    // Cancelled when the container is recycled for another card, so the old card's artwork stays out.
    private CancellationTokenSource? _artwork;
    private long _artworkShownAt;
    private bool _sizedByMeasure;
    private bool _reservesEpisodeLine;
    private ButtonBase? _host;

    public MediaCardView()
    {
        InitializeComponent();
        ArtImage.ScaleTransition = new Vector3Transition { Duration = ZoomDuration };
        Show(null);
    }

    public MediaCard? Card
    {
        get => (MediaCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>
    /// Keeps the episode title's line also on a card without one. A grid sizes every card by its first, so one holding
    /// an episode sets this on all of them, as the web's grid grows its rows to the tallest card.
    /// </summary>
    public bool ReservesEpisodeLine
    {
        get => _reservesEpisodeLine;
        set
        {
            if (_reservesEpisodeLine != value)
            {
                _reservesEpisodeLine = value;
                ShowEpisodeTitle(Card);
            }
        }
    }

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MediaCardView)sender).Show((MediaCard?)e.NewValue);

    // Every part is set for every card: containers are recycled, so nothing of the previous card may stay.
    private void Show(MediaCard? card)
    {
        // The thumbhash's blur shows at once; the title stands in only without one.
        var blur = Thumbhashes.Get(card?.ImageThumbhash);
        BlurImage.Source = blur;
        Placeholder.Text = card?.Title ?? "";
        Placeholder.Visibility = Visible(blur is null);
        _artwork?.Cancel();
        _artwork = null;
        ArtImage.OpacityTransition = null;
        ArtImage.Opacity = 0;
        ArtImage.Source = null;
        if (card?.ImageUrl is { } url)
        {
            _artwork = new CancellationTokenSource();
            _ = ShowArtworkAsync(card, url, _artwork.Token);
        }

        SetBadge(ResolutionBadge, ResolutionText, card?.ResolutionBadge);
        SetBadge(AudioBadge, AudioText, card?.AudioBadge);
        // Silo writes ATMOS as a heavy word of its own, without the speaker.
        bool atmos = card?.AudioBadge == "ATMOS";
        AudioIcon.Visibility = atmos ? Visibility.Collapsed : Visibility.Visible;
        AudioText.FontFamily = (FontFamily)Application.Current.Resources[atmos ? "SiloFontExtraBold" : "SiloFontSemiBold"];
        SetBadge(RatingBadge, RatingText, card?.RatingBadge);

        TitleText.Text = card?.Title ?? "";
        ShowEpisodeTitle(card);
        SubtitleText.Style = (Style)Application.Current.Resources[
            card?.Shape == MediaCardShape.Landscape ? "SiloCaptionTextStyle" : "SiloOverlineTextStyle"];
        SetText(SubtitleText, card?.Subtitle);
        SetText(CaptionText, card?.Caption);
        if (card?.IsCaptionAttention == true)
        {
            CaptionText.Foreground = (Brush)Application.Current.Resources["SiloAttentionBrush"];
        }
        else
        {
            // The style's colour again, for a recycled card.
            CaptionText.ClearValue(TextBlock.ForegroundProperty);
        }

        SetHovered(false);
        ShowState();
        UpdateSize(ActualWidth);
    }

    // What the viewer can change on the card, as the server or this session last left it.
    private void ShowState()
    {
        var card = Card;
        var state = card is null ? new MediaCardState(false, false, false) : _actions.StateOf(card);

        WatchedMark.Visibility = card?.ShowsWatchedMark == true && state.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        bool hasProgress = card?.Progress is > 0 && !state.IsProgressCleared;
        if (hasProgress && Progress is null)
        {
            FindName(nameof(Progress));
        }
        if (Progress is { } bar)
        {
            bar.Value = hasProgress ? Math.Clamp(card!.Progress!.Value, 0, 1) : 0;
            bar.Visibility = Visible(hasProgress);
            bar.Foreground = Brush(state.IsWatched || bar.Value >= 1 ? "SiloCompleteBrush" : "SiloPrimaryBrush");
        }
        // A TMDB title's request state can change on the card: its watchlist add requested it.
        SetBadge(StatusBadge, StatusText, card is null ? null : _actions.StatusBadgeOf(card));
        if (HoverLayer is not null)
        {
            ShowActions(card, state, hasProgress);
        }
    }

    // Makes the play disc, the card's buttons and its menu, for the pointer, the keyboard or the context menu. A row of
    // cards scrolling in took more than a frame while every card made them up front, and most cards are never hovered.
    // The caller shows the card's state on them next.
    private void EnsureActions()
    {
        if (HoverLayer is null)
        {
            FindName(nameof(HoverLayer));
            SizeActions(Art.ActualWidth > 0 ? Art.ActualWidth : ActualWidth);
        }
    }

    private void ShowActions(MediaCard? card, MediaCardState state, bool hasProgress)
    {
        var foot = new Thickness(10, 10, 10, hasProgress ? 12 : 10);
        ActionsPanel.Margin = foot;
        MoreButton.Margin = foot;
        TitleWatchlistButton.Margin = foot;

        bool titleWatchlist = card is not null && _actions.CanToggleTitleWatchlist(card);
        TitleWatchlistButton.Visibility = Visible(titleWatchlist);
        TitleWatchlistButton.IsEnabled = card is not null && !_actions.IsSending(card);
        TitleWatchlistIcon.IsChecked = state.InWatchlist;
        TitleWatchlistIcon.Foreground = Brush(state.InWatchlist ? "SiloPrimaryBrush" : "SiloForegroundBrush");
        AutomationProperties.SetName(TitleWatchlistButton,
            state.InWatchlist ? $"Remove {card?.Title} from your watchlist" : $"Add {card?.Title} to your watchlist");
        ToolTipService.SetToolTip(TitleWatchlistButton, state.InWatchlist ? "On Watchlist" : "Add to Watchlist");

        bool canPlay = card is not null && MediaCardActions.CanPlay(card);
        bool canWatch = card is not null && MediaCardActions.CanMarkWatched(card);
        bool canCollect = card is not null && MediaCardActions.CanCollect(card);
        PlayButton.Visibility = canPlay ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PlayButton, "Play " + card?.Title);

        string watched = card is null ? "" : DetailFacts.WatchedLabel(card.Type, state.IsWatched);
        // Silo's eye: open and green once watched, struck through before.
        WatchedIcon.Glyph = state.IsWatched ? "" : "";
        WatchedIcon.Foreground = Brush(state.IsWatched ? "SiloWatchedButtonBrush" : "SiloForegroundBrush");
        Label(WatchedButton, watched);
        WatchedButton.Visibility = Visible(canWatch);
        MenuWatched.Text = watched;
        MenuWatched.Visibility = Visible(canWatch);

        string favorite = state.IsFavorite ? "Remove from Favorites" : "Add to Favorites";
        FavoriteIcon.Glyph = state.IsFavorite ? "" : "";
        FavoriteIcon.Foreground = Brush(state.IsFavorite ? "SiloPrimaryBrush" : "SiloForegroundBrush");
        Label(FavoriteButton, favorite);
        FavoriteButton.Visibility = Visible(canCollect);
        MenuFavorite.Text = favorite;
        MenuFavorite.Visibility = Visible(canCollect);
        MenuWatchlist.Text = state.InWatchlist ? "Remove from Watchlist" : "Add to Watchlist";
        MenuWatchlist.Visibility = Visible(canCollect);

        bool fromBeginning = card is not null && MediaCardActions.CanPlayFromBeginning(card, state);
        bool resetProgress = card is not null && MediaCardActions.CanResetProgress(card, state);
        bool notInterested = card is not null && MediaCardActions.CanMarkNotInterested(card);
        MenuPlayFromBeginning.Visibility = Visible(fromBeginning);
        MenuResetProgress.Visibility = Visible(resetProgress);
        MenuNotInterested.Text = state.IsNotInterested ? "Show in Recommendations" : "Not Interested";
        MenuNotInterested.Visibility = Visible(notInterested);
        var row = card is null ? null : RowOf(this);
        bool canRemove = card is not null && row?.CardRemoveCommand is { } remove && remove.CanExecute(card);
        MenuRemove.Text = row?.CardRemoveLabel ?? "";
        MenuRemove.Visibility = Visible(canRemove);
        bool others = canWatch || canCollect || fromBeginning || resetProgress || notInterested;
        MenuRemoveSeparator.Visibility = Visible(canRemove && others);
        MoreButton.Visibility = Visible(others || canRemove);
    }

    // The row a card sits in offers the cards' removal (Continue Watching, Next Up).
    private static MediaRow? RowOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is MediaRow row)
            {
                return row;
            }
        }
        return null;
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static void Label(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private static void SetBadge(Border badge, TextBlock text, string? value)
    {
        text.Text = value ?? "";
        badge.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowEpisodeTitle(MediaCard? card)
    {
        SetText(EpisodeTitleText, card?.EpisodeTitle);
        bool keepsLine = _reservesEpisodeLine && string.IsNullOrEmpty(card?.EpisodeTitle) && card?.Shape == MediaCardShape.Poster;
        EpisodeLineSpacer.Visibility = keepsLine ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetText(TextBlock text, string? value)
    {
        text.Text = value ?? "";
        text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    // The artwork's height follows the width the card is measured at, so its first measure already gives its real
    // height: a grid sizes all its rows by one freshly made card when its first one is scrolled far away, and a card
    // without its artwork's height would shrink every row, and the scroll position with them.
    protected override Size MeasureOverride(Size availableSize)
    {
        _sizedByMeasure = double.IsFinite(availableSize.Width) && availableSize.Width > 0;
        if (_sizedByMeasure)
        {
            UpdateSize(availableSize.Width);
        }
        return base.MeasureOverride(availableSize);
    }

    // Only a card measured without a width follows its arranged one: following both would ping-pong between them.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_sizedByMeasure)
        {
            UpdateSize(e.NewSize.Width);
        }
    }

    private void UpdateSize(double width)
    {
        if (width <= 0)
        {
            return;
        }
        double height = Card?.Shape == MediaCardShape.Landscape ? width * 9 / 16 : width * 3 / 2;
        Art.Height = height;
        ArtImage.CenterPoint = new Vector3((float)(width / 2), (float)(height / 2), 0);
        SizeActions(width);
    }

    private void SizeActions(double width)
    {
        if (HoverLayer is null || width <= 0)
        {
            return;
        }
        bool wide = width >= WideCard;
        PlayButton.Width = PlayButton.Height = wide ? 44 : 40;
        foreach (var button in new[] { WatchedButton, FavoriteButton, MoreButton, TitleWatchlistButton })
        {
            button.Width = button.Height = wide ? 36 : 32;
        }
    }

    // The buttons take the foot, where the rating was.
    private void SetHovered(bool hovered)
    {
        ArtImage.Scale = hovered ? new Vector3(HoverScale, HoverScale, 1) : Vector3.One;
        BottomBadges.Opacity = hovered ? 0 : 1;
        if (HoverLayer is not null)
        {
            HoverLayer.Opacity = hovered ? 1 : 0;
            HoverLayer.IsHitTestVisible = hovered;
        }
    }

    // The buttons are brought up to date first: whether the server keeps TMDB titles on the watchlist may have been read
    // after the card showed. The keyboard reaches them through focus.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Card is null)
        {
            SetHovered(false);
            return;
        }
        EnsureActions();
        ShowState();
        SetHovered(true);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e) => ShowState();

    // The card's own button takes focus before Tab moves on to the buttons on the card, so they are made then.
    private void OnHostGotFocus(object sender, RoutedEventArgs e)
    {
        if (Card is not null && HoverLayer is null)
        {
            EnsureActions();
            ShowState();
        }
    }

    // The button around the card, where there is one (a card in a row or grid).
    private ButtonBase? HostButton()
    {
        var parent = VisualTreeHelper.GetParent(this);
        for (int depth = 0; parent is not null && depth < 3; depth++, parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ButtonBase button)
            {
                return button;
            }
        }
        return null;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHovered(false);

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            _actions.Play(card);
        }
    }

    private async void OnWatchedClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ToggleWatchedAsync(card);
        }
    }

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ToggleFavoriteAsync(card);
        }
    }

    private void OnPlayFromBeginningClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            _actions.PlayFromBeginning(card);
        }
    }

    private async void OnResetProgressClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ResetProgressAsync(card);
        }
    }

    private async void OnNotInterestedClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ToggleNotInterestedAsync(card);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card && RowOf(this)?.CardRemoveCommand is { } remove && remove.CanExecute(card))
        {
            remove.Execute(card);
        }
    }

    // Right-click, Shift+F10 or the menu key open the same menu as the card's ⋮, where the pointer is.
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Card is null)
        {
            return;
        }
        EnsureActions();
        ShowState();
        if (MoreButton.Visibility != Visibility.Visible)
        {
            return;
        }
        args.Handled = true;
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(this, out var point))
        {
            options.Position = point;
        }
        MoreMenu.ShowAt(this, options);
    }

    private async void OnWatchlistClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ToggleWatchlistAsync(card);
        }
    }

    private async void OnTitleWatchlistClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.ToggleTitleWatchlistAsync(card);
        }
    }

    private void OnMoreOpening(object? sender, object e) => ShowState();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _actions.Changed += OnStateChanged;
        ForgetHost();
        _host = HostButton();
        if (_host is not null)
        {
            _host.GotFocus += OnHostGotFocus;
        }
        ShowState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _actions.Changed -= OnStateChanged;
        ForgetHost();
    }

    private void ForgetHost()
    {
        if (_host is not null)
        {
            _host.GotFocus -= OnHostGotFocus;
            _host = null;
        }
    }

    private void OnStateChanged(object? sender, string contentId)
    {
        if (Card?.Id == contentId)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ShowState();
            }
            else
            {
                DispatcherQueue.TryEnqueue(ShowState);
            }
        }
    }

    // While the page scrolls fast, the artwork waits for it to slow down (ScrollMotion): cards rushing by decode nothing.
    private async Task ShowArtworkAsync(MediaCard card, Uri url, CancellationToken cancellationToken)
    {
        _artworkShownAt = Stopwatch.GetTimestamp();
        await ScrollMotion.WhenSlowAsync();
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        ArtImage.Source = card.Shape == MediaCardShape.Poster
            ? Artwork.LoadAtHeight(url, DecodeSize(double.IsNaN(Art.Height) ? Art.ActualHeight : Art.Height, PosterHeight), cancellationToken)
            : Artwork.Load(url, DecodeSize(Art.ActualWidth, LandscapeWidth), cancellationToken);
    }

    private static int DecodeSize(double shown, double fallback) => (int)Math.Ceiling(shown > 0 ? shown : fallback);

    // Artwork that came at once shows at once; artwork the viewer waited for fades in over its blur, as on Android.
    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        Placeholder.Visibility = Visibility.Collapsed;
        bool waited = Stopwatch.GetElapsedTime(_artworkShownAt) > InstantReveal;
        ArtImage.OpacityTransition = waited ? new ScalarTransition { Duration = RevealDuration } : null;
        ArtImage.Opacity = 1;
    }

    // A missing or expired artwork URL leaves the blur, or without one the title, in place.
    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => ArtImage.Source = null;
}
