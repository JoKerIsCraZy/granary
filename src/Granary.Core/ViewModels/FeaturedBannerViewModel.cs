using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Artwork;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

/// <summary>A slide of Home's banner: a card of the featured row, as the hero shows it.</summary>
public sealed record FeaturedSlide(BrowseHeroTitle Title, CatalogItem Item)
{
    /// <summary>
    /// What Play plays: a movie or episode itself, a series or season the episode the server names for it; null when there
    /// is nothing to play, as the Mac app shows Play only for what plays.
    /// </summary>
    public string? PlayContentId =>
        Item.PlayContentId is { Length: > 0 } next ? next
        : Item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode ? Item.ContentId
        : null;
}

/// <summary>
/// Home's banner (docs/ui-design.md, Home), as the web's <c>components/HeroBanner.tsx</c>: the cards of the first row the
/// server marks as the hero (<c>featured</c>), one slide at a time at the top of Home, with Play and More Info. With several
/// slides it moves on every <see cref="AdvanceTime"/>, from the last back to the first, while Home shows it in view. The
/// pointer or keyboard focus on it holds it for as long as they stay (<see cref="Hold"/>); its pause button stops it until pressed
/// again, also across visits to other pages (<see cref="IsPausedByButton"/>); with animations off it moves only by hand.
/// Both clear when the banner goes. Each new slide and each resume start a fresh countdown (<see cref="Cycle"/>). While it
/// is in view, the next slide's pictures are fetched ahead, as the web preloads the next backdrop.
/// </summary>
public sealed partial class FeaturedBannerViewModel : ObservableObject
{
    /// <summary>How long a slide shows before the next: the web's 8 s, the Mac app's too.</summary>
    public static readonly TimeSpan AdvanceTime = TimeSpan.FromSeconds(8);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly INavigationService _navigation;
    private readonly IArtworkPrefetcher? _artwork;
    private IReadOnlyList<FeaturedSlide> _slides = [];
    private int _index;
    private ITimer? _countdown;
    // Each countdown; one that fires after another started does nothing.
    private int _generation;
    private bool _isActive;
    private BrowseHeroTitle? _fetchedAhead;

    internal FeaturedBannerViewModel(IUiDispatcher dispatcher, TimeProvider time, INavigationService navigation, IArtworkPrefetcher? artwork)
    {
        _dispatcher = dispatcher;
        _time = time;
        _navigation = navigation;
        _artwork = artwork;
    }

    /// <summary>The slides; empty while Home has no featured row with cards, and the hero follows the cards instead.</summary>
    public IReadOnlyList<FeaturedSlide> Slides => _slides;

    /// <summary>The slide shown, from 0.</summary>
    public int Index => _index;

    /// <summary>Held while the pointer or focus is on the banner.</summary>
    [ObservableProperty]
    public partial bool IsHeld { get; private set; }

    /// <summary>Stopped with the pause button, until it is pressed again or the banner goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool IsPausedByButton { get; private set; }

    /// <summary>A countdown to the next slide runs: the progress rail fills meanwhile.</summary>
    [ObservableProperty]
    public partial bool IsAdvancing { get; private set; }

    /// <summary>
    /// Counts the slides shown and the countdowns started: the progress rail starts empty again with each, and fills while
    /// <see cref="IsAdvancing"/>.
    /// </summary>
    [ObservableProperty]
    public partial int Cycle { get; private set; }

    public bool HasSlides => _slides.Count > 0;

    /// <summary>The arrows, the counter and the pause button show only with several slides, as on the web.</summary>
    public bool HasSeveral => _slides.Count > 1;

    public FeaturedSlide? Current => _slides.Count > 0 ? _slides[Math.Clamp(_index, 0, _slides.Count - 1)] : null;

    /// <summary>The slide shown, two digits as the web's counter and "No. 01"; null without slides, and the hero then says none.</summary>
    public string? NumberText => HasSlides ? TwoDigits(_index + 1) : null;

    public string CountText => TwoDigits(_slides.Count);

    public bool CanPlay => Current?.PlayContentId is not null;

    public string PauseLabel => IsPausedByButton ? "Play slideshow" : "Pause slideshow";

    /// <summary>Whether the banner is on screen: Home scrolled down past it folds it away, and it holds still meanwhile.</summary>
    public bool IsInView
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Restart();
            }
        }
    } = true;

    /// <summary>Windows' animations are off: the slides change only by hand, as the web's under reduced motion.</summary>
    public bool IsMotionReduced
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Restart();
            }
        }
    }

    /// <summary>
    /// The pointer or focus came onto the banner, or left it: the slide shown stays while they are on it, and letting go
    /// starts a fresh countdown, unless the pause button stopped the slideshow. A banner without slides is never held.
    /// </summary>
    public void Hold(bool held)
    {
        held &= HasSlides;
        if (IsHeld != held)
        {
            IsHeld = held;
            Restart();
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPausedByButton = !IsPausedByButton;
        Restart();
    }

    [RelayCommand]
    private void Next() => Step(1);

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Play()
    {
        if (Current?.PlayContentId is { } contentId)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(contentId));
        }
    }

    [RelayCommand]
    private void MoreInfo()
    {
        if (Current is { } slide)
        {
            _navigation.NavigateTo(AppRoute.Item, slide.Item.ContentId);
        }
    }

    /// <summary>Home is shown, or left: the slideshow runs only while it is, and nothing holds it once Home is left.</summary>
    internal void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }
        _isActive = isActive;
        if (!isActive)
        {
            IsHeld = false;
        }
        Restart();
    }

    /// <summary>
    /// The featured row loaded (again), or its slides were made again for the spoiler switches: the slide shown stays, with its
    /// countdown, while the row still has it; else the first slide shows. A banner that goes lets go of what held or paused it.
    /// </summary>
    internal void Show(IReadOnlyList<FeaturedSlide> slides)
    {
        string? shown = Current?.Title.Key;
        bool wasAdvancing = IsAdvancing;
        int index = shown is null ? -1 : IndexOf(slides, shown);
        ShowSlides(slides, Math.Max(index, 0));
        if (slides.Count == 0)
        {
            IsHeld = false;
            IsPausedByButton = false;
        }
        if (index < 0)
        {
            Restart(newSlide: slides.Count > 0);
        }
        else if (wasAdvancing != ShouldAdvance)
        {
            Restart();
        }
        else
        {
            FetchNext();
        }
    }

    private bool ShouldAdvance => HasSeveral && !IsHeld && !IsPausedByButton && _isActive && IsInView && !IsMotionReduced;

    // Takes the slides and the slide shown together, then says so once: a slide read between the two would show in the hero
    // for a moment, its backdrop fading in and out again.
    private void ShowSlides(IReadOnlyList<FeaturedSlide> slides, int index)
    {
        bool slidesChanged = !ReferenceEquals(_slides, slides);
        bool indexChanged = _index != index;
        if (!slidesChanged && !indexChanged)
        {
            return;
        }
        _slides = slides;
        _index = index;
        if (slidesChanged)
        {
            OnPropertyChanged(nameof(Slides));
        }
        if (indexChanged)
        {
            OnPropertyChanged(nameof(Index));
        }
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(NumberText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasSlides));
        OnPropertyChanged(nameof(HasSeveral));
        OnPropertyChanged(nameof(CanPlay));
    }

    private void Step(int by)
    {
        if (HasSlides)
        {
            ShowSlides(_slides, (_index + by + _slides.Count) % _slides.Count);
            Restart(newSlide: true);
        }
    }

    // Stops the countdown running and starts a fresh one when the slideshow moves on by itself. A new slide, also one that
    // waits, starts the rail empty again.
    private void Restart(bool newSlide = false)
    {
        _generation++;
        _countdown?.Dispose();
        _countdown = null;
        IsAdvancing = ShouldAdvance;
        if (IsAdvancing || newSlide)
        {
            Cycle++;
        }
        if (IsAdvancing)
        {
            int generation = _generation;
            _countdown = _time.CreateTimer(_ => _dispatcher.Post(() => Advance(generation)), null, AdvanceTime, Timeout.InfiniteTimeSpan);
        }
        FetchNext();
    }

    private void Advance(int generation)
    {
        if (generation == _generation)
        {
            Step(1);
        }
    }

    // The next slide's backdrop and logo, so they show at once when it comes; only while the banner is in view.
    private void FetchNext()
    {
        if (_artwork is null || !HasSeveral || !_isActive || !IsInView)
        {
            return;
        }
        var next = _slides[(_index + 1) % _slides.Count].Title;
        if (next != _fetchedAhead)
        {
            _fetchedAhead = next;
            _artwork.Prefetch(new[] { next.BackdropUrl, next.LogoUrl }.OfType<Uri>());
        }
    }

    private static int IndexOf(IReadOnlyList<FeaturedSlide> slides, string key)
    {
        for (int i = 0; i < slides.Count; i++)
        {
            if (slides[i].Title.Key == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static string TwoDigits(int number) => number.ToString("00", CultureInfo.InvariantCulture);
}
