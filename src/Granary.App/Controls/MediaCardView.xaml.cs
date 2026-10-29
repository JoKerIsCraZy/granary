using System.Diagnostics;
using System.Numerics;
using Granary.Core.Overlays;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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

    public static readonly DependencyProperty PickProperty =
        DependencyProperty.Register(nameof(Pick), typeof(bool?), typeof(MediaCardView), new PropertyMetadata(null, (d, _) => ((MediaCardView)d).ShowPick()));

    // Artwork is decoded as large as its box shows it; until the card has been laid out, as a row's cards are.
    private const double PosterHeight = 320;
    private const double LandscapeWidth = 400;
    // A card's width changed by more than this share of the width its artwork was decoded for decodes it again.
    private const double RedecodeChange = 0.1;

    // How long Request shows under a pointer before the pointer can take it: past its 150 ms fade-in.
    private static readonly TimeSpan ArmTime = TimeSpan.FromMilliseconds(200);

    // Android's: artwork that comes this soon was never waited on and shows at once, later artwork fades in over its blur.
    private static readonly TimeSpan InstantReveal = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(300);

    // Silo's web: the artwork grows 6 % under the pointer, over 300 ms. A poster gets a 40 px disc and 32 px buttons,
    // a wide card 44 and 36; the buttons sit just above a progress bar.
    private const float HoverScale = 1.06f;
    private const double WideCard = 250;
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(300);
    // The web's inset of the badges from the artwork's edges, and of an edge's two corners from each other.
    private const double EdgeInset = 8;
    // Between the artwork and the title.
    private const double CaptionGap = 11;

    // The resources cards use, by key (Resource). UI thread only.
    private static readonly Dictionary<string, object> SharedResources = new(StringComparer.Ordinal);

    private readonly MediaCardActions _actions = App.Services.GetRequiredService<MediaCardActions>();
    private readonly CardOverlaySettings _overlays = App.Services.GetRequiredService<CardOverlaySettings>();

    // Cancelled when the container is recycled for another card, so the old card's artwork stays out.
    private CancellationTokenSource? _artwork;
    private long _artworkShownAt;
    private bool _sizedByMeasure;
    private double _artWidth;
    // Done at the card's first size, which a card just made waits for before it decodes its artwork.
    private TaskCompletionSource? _sized;
    // The shown card's artwork, and the width it was decoded for (0 before it was), to decode it again at another width.
    private Uri? _artworkUrl;
    private double _decodedWidth;
    // The card and badges the corners show, so a card loaded again keeps its badges rather than making them anew.
    private MediaCard? _badgedCard;
    private CardOverlayPrefs? _badgedPrefs;
    private OverlayPreset? _badgedPreset;
    // What the subtitle's style and the caption's colour are set for: null before the first card.
    private bool? _subtitleLandscape;
    private bool _captionAttention;
    // The shown card's shape and whether its progress bar shows, which size the artwork and place the badges; and what
    // they were last done for.
    private bool _landscape;
    private bool _lifted;
    // Under the pointer: the hover layer shows.
    private bool _hovered;
    // The pointer over the card may show Request: a mouse, or a touch or pen on a card that already had focus.
    private bool _pointerMayRequest;
    // Request has shown long enough for a pointer to take it (ArmTime).
    private bool _requestArmed;
    private DispatcherQueueTimer? _armTimer;
    private string? _requestName;
    private bool _followsHostFocus;
    private bool _sizedLandscape;
    private (double Width, bool Landscape, bool Lifted) _placed;
    // The badge views in each corner (top left, top right, bottom left, bottom right), ahead of its fixed children: a
    // recycled card compares them here rather than asking the corner for its children.
    private readonly List<BadgeView>[] _cornerViews = [[], [], [], []];
    private bool _reservesEpisodeLine;
    private CardCaption _caption = CardCaption.TitleMetadata;
    private ButtonBase? _host;
    // The entries the card's list adds to its menu, and the menu items made for them.
    private IReadOnlyList<CardMenuEntry> _entries = [];
    private readonly List<MenuFlyoutItemBase> _entryItems = [];
    private bool _hasOwnEntries;
    // The grid the card sits in, found once: a card stays in its grid while it is reused.
    private MediaGrid? _grid;
    private bool _gridFound;

    public MediaCardView()
    {
        InitializeComponent();
        ArtImage.ScaleTransition = new Vector3Transition { Duration = ZoomDuration };
        TopBadges.Gap = BottomBadges.Gap = EdgeInset;
        Show(null);
    }

    public MediaCard? Card
    {
        get => (MediaCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>
    /// While the card's grid selects: true when the card is picked, false when not; null when the grid does not select.
    /// A selecting card shows its pick mark and no play disc or buttons, as the web's.
    /// </summary>
    public bool? Pick
    {
        get => (bool?)GetValue(PickProperty);
        set => SetValue(PickProperty, value);
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

    /// <summary>
    /// What shows under the artwork, the profile's choice (<see cref="CardPresentation"/>), set by the row or grid the card is
    /// in. A card elsewhere (an episode on a title's page) keeps its title and details, as the web's.
    /// </summary>
    public CardCaption Caption
    {
        get => _caption;
        set
        {
            if (_caption != value)
            {
                _caption = value;
                ShowCaption(Card);
            }
        }
    }

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MediaCardView)sender).Show((MediaCard?)e.NewValue);

    // Every part is set for every card: containers are recycled, so nothing of the previous card may stay.
    private void Show(MediaCard? card)
    {
        _landscape = card?.Shape == MediaCardShape.Landscape;
        // The thumbhash's blur shows at once; the title stands in only without one.
        var blur = Thumbhashes.Get(card?.ImageThumbhash);
        BlurImage.Source = blur;
        Placeholder.Text = card?.Title ?? "";
        Placeholder.Visibility = Visible(blur is null);
        _artwork?.Cancel();
        _artwork = null;
        _artworkUrl = card?.ImageUrl;
        _decodedWidth = 0;
        ArtImage.OpacityTransition = null;
        ArtImage.Opacity = 0;
        ArtImage.Source = null;
        if (_artworkUrl is { } url)
        {
            _artwork = new CancellationTokenSource();
            _ = ShowArtworkAsync(url, _artwork.Token);
        }

        ShowBadges(card);

        TitleText.Text = card?.Title ?? "";
        // A recycled card mostly shows the same shape and colours: each is set only when it changes.
        if (_subtitleLandscape != _landscape)
        {
            _subtitleLandscape = _landscape;
            SubtitleText.Style = Resource<Style>(_landscape ? "SiloCaptionTextStyle" : "SiloOverlineTextStyle");
        }
        ShowCaption(card);
        bool attention = card?.IsCaptionAttention == true;
        if (_captionAttention != attention)
        {
            _captionAttention = attention;
            if (attention)
            {
                CaptionText.Foreground = Brush("SiloAttentionBrush");
            }
            else
            {
                // The style's colour again, for a recycled card.
                CaptionText.ClearValue(TextBlock.ForegroundProperty);
            }
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
        _lifted = hasProgress;
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
        PlaceBadges();
        if (HoverLayer is not null)
        {
            ShowActions(card, state, hasProgress);
        }
        ShowRequest(card);
    }

    // A TMDB title's Request: under the mouse while the viewer may request it, with keyboard focus on the card (the web's
    // focus-within), and in view, saying Sending, while its request is on its way. Made only for a card hovered or focused
    // while it can be requested, or one whose request is on its way; none in a grid that selects.
    private void ShowRequest(MediaCard? card)
    {
        // Most cards are a library's or cannot be requested: nothing to look up for them.
        bool requesting = card?.RequestOffer is not null && _actions.HasRequestsInFlight && _actions.IsRequesting(card);
        if (RequestButton is null)
        {
            if (!requesting)
            {
                return;
            }
            MakeRequestButton();
        }
        bool offered = Pick is null && card?.RequestOffer is not null && (requesting || _actions.CanRequest(card));
        if (!offered)
        {
            // Requested from the keyboard: focus goes back to the card, not to nowhere.
            if (RequestButton!.FocusState != FocusState.Unfocused)
            {
                _host?.Focus(FocusState.Programmatic);
            }
            RequestButton.Visibility = Visibility.Collapsed;
            Disarm();
            return;
        }
        RequestButton!.Visibility = Visibility.Visible;
        RequestButton.IsEnabled = !requesting;
        RequestLabel.Text = _actions.RequestActionText(card!);
        string name = _actions.RequestActionName(card!);
        if (name != _requestName)
        {
            _requestName = name;
            AutomationProperties.SetName(RequestButton, name);
        }
        RequestPlus.Visibility = Visible(!requesting);
        RequestSpinner.Visibility = Visible(requesting);
        RequestSpinner.IsActive = requesting;
        ShowRequestPresence();
    }

    private void MakeRequestButton()
    {
        FindName(nameof(RequestButton));
        RequestButton!.GotFocus += OnRequestFocusChanged;
        RequestButton.LostFocus += OnRequestFocusChanged;
        RequestButton.LosingFocus += OnRequestLosingFocus;
        FollowHostFocus();
    }

    // The card's own focus shows the pill too, and its Tab leads to it; followed only for a card that has one.
    private void FollowHostFocus()
    {
        if (_host is not null && !_followsHostFocus)
        {
            _followsHostFocus = true;
            _host.LostFocus += OnRequestFocusChanged;
            _host.LosingFocus += OnHostLosingFocus;
        }
    }

    // A row or grid takes Tab once and gives it on past its cards (the arrows move between them), so Tab from a card whose
    // Request shows goes to the pill first, and Shift+Tab from the pill back to the card. LosingFocus bubbles: the host
    // also hears the pill's, which must go on where Tab sends it.
    private void OnHostLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (ReferenceEquals(args.OldFocusedElement, sender) && args.Direction == FocusNavigationDirection.Next
            && args.InputDevice == FocusInputDeviceKind.Keyboard
            && RequestButton is { Visibility: Visibility.Visible, IsTabStop: true } pill && !ReferenceEquals(args.NewFocusedElement, pill))
        {
            args.TrySetNewFocusedElement(pill);
        }
    }

    private void OnRequestLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (ReferenceEquals(args.OldFocusedElement, sender) && args.Direction == FocusNavigationDirection.Previous
            && args.InputDevice == FocusInputDeviceKind.Keyboard && _host is { } host && !ReferenceEquals(args.NewFocusedElement, host))
        {
            args.TrySetNewFocusedElement(host);
        }
    }

    // Shown under the mouse, with keyboard focus on the card or the pill, or while its request is on its way. A pointer
    // reaches it only once it has shown for ArmTime: a quick click as it fades in opens the card, as does the first tap of
    // a touch or pen, which shows it only on a card that already had focus. The keyboard reaches it while it shows.
    private void ShowRequestPresence()
    {
        if (RequestButton is not { Visibility: Visibility.Visible } button)
        {
            return;
        }
        bool requesting = Card is { } card && _actions.HasRequestsInFlight && _actions.IsRequesting(card);
        bool keyboard = HasKeyboardFocus();
        bool pointer = _hovered && _pointerMayRequest;
        button.Opacity = requesting || keyboard || pointer ? 1 : 0;
        button.IsTabStop = keyboard && !requesting;
        button.IsHitTestVisible = !requesting && (keyboard || (pointer && _requestArmed));
        if (pointer && !_requestArmed)
        {
            ArmLater();
        }
        else if (!pointer)
        {
            Disarm();
        }
    }

    private bool HasKeyboardFocus() =>
        _host?.FocusState is FocusState.Keyboard or FocusState.Programmatic
        || RequestButton?.FocusState is FocusState.Keyboard or FocusState.Programmatic;

    private void OnRequestFocusChanged(object sender, RoutedEventArgs e) => ShowRequestPresence();

    private void ArmLater()
    {
        if (_armTimer is null)
        {
            _armTimer = DispatcherQueue.CreateTimer();
            _armTimer.Interval = ArmTime;
            _armTimer.IsRepeating = false;
            _armTimer.Tick += (_, _) =>
            {
                _requestArmed = true;
                ShowRequestPresence();
            };
        }
        if (!_armTimer.IsRunning)
        {
            _armTimer.Start();
        }
    }

    private void Disarm()
    {
        _requestArmed = false;
        _armTimer?.Stop();
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
        if (RequestButton is null && Pick is null && Card is { RequestOffer: not null } card && _actions.CanRequest(card))
        {
            MakeRequestButton();
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
        if (!_gridFound && row is null && card is not null && VisualTreeHelper.GetParent(this) is not null)
        {
            _grid = GridOf(this);
            _gridFound = true;
        }
        var grid = row is null ? _grid : null;
        bool canRemove = card is not null && row?.CardRemoveCommand is { } remove && remove.CanExecute(card);
        MenuRemove.Text = row?.CardRemoveLabel ?? "";
        MenuRemove.Visibility = Visible(canRemove);
        bool others = canWatch || canCollect || fromBeginning || resetProgress || notInterested;
        MenuRemoveSeparator.Visibility = Visible(canRemove && others);
        // Only a grid with entries of its own asks for them; every other card skips this.
        _entries = card is not null && grid?.CardMenu is { } source ? source.EntriesFor(card) : [];
        _hasOwnEntries = others || canRemove;
        MoreButton.Visibility = Visible(others || canRemove || _entries.Count > 0);
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

    // The grid a card sits in adds its list's own entries (a collection's Edit and Delete).
    private static MediaGrid? GridOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is MediaGrid grid)
            {
                return grid;
            }
        }
        return null;
    }

    // The list's entries at the menu's foot, after a line when the card's own are there too.
    private void ShowEntries()
    {
        foreach (var item in _entryItems)
        {
            MoreMenu.Items.Remove(item);
        }
        _entryItems.Clear();
        if (_entries.Count == 0 || Card is not { } card)
        {
            return;
        }
        if (_hasOwnEntries)
        {
            _entryItems.Add(new MenuFlyoutSeparator());
        }
        foreach (var entry in _entries)
        {
            _entryItems.Add(entry.IsChecked is { } isChecked
                ? new ToggleMenuFlyoutItem { Text = entry.Label, IsChecked = isChecked, Command = entry.Command, CommandParameter = card }
                : new MenuFlyoutItem { Text = entry.Label, Command = entry.Command, CommandParameter = card });
        }
        foreach (var item in _entryItems)
        {
            MoreMenu.Items.Add(item);
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Brush(string key) => Resource<Brush>(key);

    // The app's styles and brushes, looked up once: each lookup made a new wrapper of the resource, and with it work for
    // the collector, for every card shown. The accent recolours its brushes in place (AccentPainter), so they stay right.
    private static T Resource<T>(string key) where T : class
    {
        if (!SharedResources.TryGetValue(key, out object? value))
        {
            value = Application.Current.Resources[key];
            SharedResources[key] = value;
        }
        return (T)value;
    }

    private static void Label(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    // The profile's badges for this card, in its style; none while they load or are off. The card's own status stays first
    // in the top right corner, the watched mark last.
    private void ShowBadges(MediaCard? card)
    {
        if (ReferenceEquals(card, _badgedCard) && ReferenceEquals(_overlays.Shown, _badgedPrefs) && card is not null)
        {
            return;
        }
        _badgedCard = card;
        _badgedPrefs = _overlays.Shown;
        var badges = card?.Overlay is { } overlay && _overlays.Shown is { } prefs ? CardBadges.Resolve(overlay, prefs) : [];
        var preset = OverlayPreset.Of(_overlays.Shown?.Preset ?? OverlayPresetId.Classic);
        bool restyled = !ReferenceEquals(preset, _badgedPreset);
        _badgedPreset = preset;
        Fill(TopLeftBadges, _cornerViews[0], OverlayPosition.TopLeft);
        Fill(TopRightBadges, _cornerViews[1], OverlayPosition.TopRight);
        Fill(BottomLeftBadges, _cornerViews[2], OverlayPosition.BottomLeft);
        Fill(BottomRightBadges, _cornerViews[3], OverlayPosition.BottomRight);

        // Shows the corner's badges before its fixed children (StatusBadge and WatchedMark top right), in the views already
        // there where they draw the same: a recycled card mostly shows the same kinds of badge, and making them anew for
        // each card took most of a scrolled card's time. The corner itself is touched only where its badges change.
        void Fill(StackPanel corner, List<BadgeView> views, OverlayPosition position)
        {
            if (restyled)
            {
                corner.Spacing = preset.StackGap;
            }
            bool alignRight = position is OverlayPosition.TopRight or OverlayPosition.BottomRight;
            int at = 0;
            foreach (var badge in badges)
            {
                if (badge.Position != position)
                {
                    continue;
                }
                var shown = at < views.Count ? views[at] : null;
                var view = CardBadgeViews.Reuse(shown, badge, preset, alignRight);
                if (shown is null)
                {
                    corner.Children.Insert(at, view.View);
                    views.Add(view);
                }
                else if (!ReferenceEquals(view, shown))
                {
                    corner.Children[at] = view.View;
                    views[at] = view;
                }
                at++;
            }
            while (views.Count > at)
            {
                corner.Children.RemoveAt(at);
                views.RemoveAt(at);
            }
        }
    }

    // A poster's badges are laid out at the web's 185 px poster and scaled to its width, insets and all; a wide card
    // keeps them at their size. Over a progress bar the bottom row rises clear of it, as the web's does.
    private void PlaceBadges()
    {
        double width = _artWidth;
        if (width <= 0)
        {
            return;
        }
        // A recycled card of the same size and progress keeps them where they are.
        var placed = (width, _landscape, _lifted);
        if (placed == _placed)
        {
            return;
        }
        _placed = placed;
        double scale = _landscape ? 1 : width / OverlayPreset.ReferenceWidth;
        double inset = EdgeInset * scale;
        double edgeWidth = Math.Max(0, (width - 2 * inset) / scale);
        bool lifted = _lifted;
        // The layout slot holds the edge at its unscaled width, and on a poster wider than the web's also as drawn: a slot
        // narrower than either clips the right corner. On a small poster the margin goes negative on purpose, so the slot
        // is the unscaled width; on a big one it is the inset, so the slot is the scaled-up edge.
        double right = width - inset - edgeWidth * Math.Max(scale, 1);
        TopBadges.Width = BottomBadges.Width = edgeWidth;
        TopBadges.Margin = new Thickness(inset, inset, right, 0);
        BottomBadges.Margin = new Thickness(inset, 0, right, inset + (lifted ? EdgeInset : 0));
        TopScale.ScaleX = TopScale.ScaleY = BottomScale.ScaleX = BottomScale.ScaleY = scale;
    }

    // The profile chose other badges, or they arrived: from any thread the setting changes on, here on the card's.
    private void OnOverlaysChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardOverlaySettings.Shown))
        {
            DispatcherQueue.TryEnqueue(() => ShowBadges(Card));
        }
    }

    private static void SetBadge(Border badge, TextBlock text, string? value)
    {
        text.Text = value ?? "";
        badge.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    // The lines under the artwork, as the profile chose them: the title and its details, the title only, or none.
    private void ShowCaption(MediaCard? card)
    {
        bool details = _caption == CardCaption.TitleMetadata;
        CaptionPanel.Visibility = Visible(_caption != CardCaption.Artwork);
        LayoutRoot.RowSpacing = _caption == CardCaption.Artwork ? 0 : CaptionGap;
        ShowEpisodeTitle(card);
        SetText(SubtitleText, details ? card?.Subtitle : null);
        SetText(CaptionText, details ? card?.Caption : null);
    }

    private void ShowEpisodeTitle(MediaCard? card)
    {
        bool details = _caption == CardCaption.TitleMetadata;
        SetText(EpisodeTitleText, details ? card?.EpisodeTitle : null);
        bool keepsLine = details && _reservesEpisodeLine && string.IsNullOrEmpty(card?.EpisodeTitle) && card?.Shape == MediaCardShape.Poster;
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
        // Measured again at the same width, as every card is while its grid lays out, it is already sized.
        if (width <= 0 || (width == _artWidth && _landscape == _sizedLandscape))
        {
            return;
        }
        _sizedLandscape = _landscape;
        double height = _landscape ? width * 9 / 16 : width * 3 / 2;
        Art.Height = height;
        _artWidth = width;
        if (_sized is { } sized)
        {
            _sized = null;
            sized.TrySetResult();
        }
        RedecodeIfResized();
        PlaceBadges();
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

    private void ShowPick()
    {
        // A grid that selects offers no Request: a click there picks the card.
        if (RequestButton is not null)
        {
            ShowRequest(Card);
        }
        if (Pick is not { } picked)
        {
            if (PickMark is not null)
            {
                PickMark.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (PickMark is null)
        {
            FindName(nameof(PickMark));
        }
        SetHovered(false);
        PickMark!.Visibility = Visibility.Visible;
        PickDot.Background = picked ? Brush("SiloPrimaryBrush") : null;
        PickDot.BorderBrush = picked ? Brush("SiloPrimaryBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        PickCheck.Visibility = Visible(picked);
    }

    // The buttons take the foot, where the rating was.
    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        if (!hovered)
        {
            _pointerMayRequest = false;
        }
        ArtImage.Scale = hovered ? new Vector3(HoverScale, HoverScale, 1) : Vector3.One;
        BottomBadges.Opacity = hovered ? 0 : 1;
        if (HoverLayer is not null)
        {
            HoverLayer.Opacity = hovered ? 1 : 0;
            HoverLayer.IsHitTestVisible = hovered;
        }
        ShowRequestPresence();
    }

    // The buttons are brought up to date first: whether the server keeps TMDB titles on the watchlist may have been read
    // after the card showed. The keyboard reaches them through focus.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Card is null || Pick is not null)
        {
            SetHovered(false);
            return;
        }
        // A touch or pen comes in just before its tap: Request shows for it only on a card that already had focus.
        _pointerMayRequest = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse || _host?.FocusState is not (null or FocusState.Unfocused);
        EnsureActions();
        ShowState();
        SetHovered(true);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e) => ShowState();

    // The card's own button takes focus before Tab moves on to the buttons on the card, so they are made then; a TMDB
    // title's Request shows while the card has keyboard focus.
    private void OnHostGotFocus(object sender, RoutedEventArgs e)
    {
        if (Card is not null && (HoverLayer is null || (RequestButton is null && Card.RequestOffer is not null)))
        {
            EnsureActions();
            ShowState();
        }
        ShowRequestPresence();
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

    // RequestAsync catches every failure itself, so nothing escapes this async void.
    private async void OnRequestClick(object sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            await _actions.RequestAsync(card);
        }
    }

    private void OnMoreOpening(object? sender, object e)
    {
        ShowState();
        ShowEntries();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _actions.Changed += OnStateChanged;
        _overlays.PropertyChanged += OnOverlaysChanged;
        ShowBadges(Card);
        ForgetHost();
        _host = HostButton();
        if (_host is not null)
        {
            _host.GotFocus += OnHostGotFocus;
            if (RequestButton is not null)
            {
                FollowHostFocus();
            }
        }
        ShowState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _actions.Changed -= OnStateChanged;
        _overlays.PropertyChanged -= OnOverlaysChanged;
        ForgetHost();
    }

    private void ForgetHost()
    {
        if (_host is not null)
        {
            _host.GotFocus -= OnHostGotFocus;
            if (_followsHostFocus)
            {
                _followsHostFocus = false;
                _host.LostFocus -= OnRequestFocusChanged;
                _host.LosingFocus -= OnHostLosingFocus;
            }
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

    // While the page scrolls fast, the artwork waits for it to slow down (ScrollMotion): cards rushing by decode nothing. A
    // card just made also waits for its first measure, so its artwork is decoded at the size it shows, not a guess: a grid's
    // new cards were decoded half again as large.
    private async Task ShowArtworkAsync(Uri url, CancellationToken cancellationToken)
    {
        _artworkShownAt = Stopwatch.GetTimestamp();
        await ScrollMotion.WhenSlowAsync();
        // A card of another shape than the last one it showed is not sized for this one yet either.
        if ((_artWidth <= 0 || _landscape != _sizedLandscape) && !cancellationToken.IsCancellationRequested)
        {
            _sized ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // A card that never gets a size (scrolled away before it was laid out) lets go of the wait with its card.
                await _sized.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            LoadArtwork(url, cancellationToken);
        }
    }

    // The artwork decoded as large as the card shows it now: a poster as high, a wide card as wide.
    private void LoadArtwork(Uri url, CancellationToken cancellationToken)
    {
        _decodedWidth = _artWidth;
        ArtImage.Source = _landscape
            ? Artwork.Load(url, DecodeSize(_artWidth, LandscapeWidth), cancellationToken)
            : Artwork.LoadAtHeight(url, DecodeSize(_artWidth * 3 / 2, PosterHeight), cancellationToken);
    }

    // A card whose artwork is in, or on its way, and whose width then changed by more than a tenth (another poster size, a
    // breakpoint crossed, a row's card given its width after it was filled) decodes it again at the new size: smaller would
    // show it blurred, larger would hold memory for nothing. A smaller change, as a window resized by a few pixels, keeps it.
    private void RedecodeIfResized()
    {
        if (_decodedWidth <= 0 || _artworkUrl is not { } url || Math.Abs(_artWidth - _decodedWidth) <= _decodedWidth * RedecodeChange)
        {
            return;
        }
        _artwork?.Cancel();
        _artwork = new CancellationTokenSource();
        _artworkShownAt = Stopwatch.GetTimestamp();
        LoadArtwork(url, _artwork.Token);
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
