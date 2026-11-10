using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Artwork;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Playback.Themes;
using Granary.Core.Servers;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

public enum ItemDetailState
{
    Loading,
    Ready,

    /// <summary>The server answered 404: the item is gone or outside the profile's libraries (it does not say which).</summary>
    NotFound,

    Failed,
}

/// <summary>
/// The detail page of a movie, series, season or episode (docs/api-contract.md 7.6, 7.7, 7.11), worded like
/// Silo's web client. The item comes first; its seasons, episodes and "More like this" follow in parallel,
/// and one that fails only hides its section. Watched, favourite, watchlist and the stars change at once and roll
/// back when the server refuses.
/// </summary>
/// <remarks>
/// Only <see cref="LoadCommand"/> is cancellable. The toggles take no token on purpose: a cancellable command
/// cancels its running call when it is executed again, and marking watched is non-retryable.
/// </remarks>
public sealed partial class ItemDetailViewModel : ObservableObject
{
    public const string NotFoundTitle = "This title is no longer available";
    public const string NotFoundHint = "It may have been removed, or you may not have access to it. Use Back to return.";
    public const string NoEpisodesText = "No episodes are available for this season yet.";

    private const int SimilarLimit = 12;

    // Twenty seasons or so of a long-running show; any further season loads its stills when it is opened.
    private const int MaxPrefetchedStills = 400;

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IArtworkPrefetcher _artwork;
    private readonly ILogger<ItemDetailViewModel> _logger;
    private readonly ViewerStateChanges? _changes;
    private readonly TrackPreferencesApi? _trackPreferences;
    private readonly TrackChoiceWrites? _trackChoices;
    private readonly EpisodeSpoilerSettings? _spoilers;
    private readonly TitlePageSettings? _titlePage;
    private readonly ThemeMusic? _themeMusic;
    private readonly IUiDispatcher? _dispatcher;
    private readonly TimeProvider _time;
    private readonly RatingWrites? _ratings;
    private readonly SeriesAirings? _airings;
    private readonly EpisodeSpans? _spans;
    private readonly BoxSetsApi? _boxSets;
    private readonly BoxSetLibraries? _boxSetLibraries;

    // A newer load makes the answers of older calls stale.
    private int _generation;
    private CatalogItemDetail? _item;

    // The item's content id for the calls about it: the server's, or the requested one if the answer had none.
    private string _contentId = "";
    private Uri? _serverBase;
    private WatchRollup? _watch;
    private IReadOnlyList<Season> _seasons = [];
    private IReadOnlyList<Episode> _episodes = [];
    // The season whose episodes the page lists, and an episode's siblings in More Episodes.
    private string? _episodesSeasonId;
    private IReadOnlyList<Episode> _siblings = [];
    private ImageParameters _imageParameters = ImageParameters.None;

    // The page lists episodes: a season, or a series with a single season.
    private bool _listsEpisodes;
    private bool _logoFailed;

    public ItemDetailViewModel(
        ISessionManager session,
        CatalogApi catalog,
        ImageParameterCache images,
        INavigationService navigation,
        IArtworkPrefetcher artwork,
        ILogger<ItemDetailViewModel> logger,
        ViewerStateChanges? changes = null,
        TrackPreferencesApi? trackPreferences = null,
        TrackChoiceWrites? trackChoices = null,
        EpisodeSpoilerSettings? spoilers = null,
        TitlePageSettings? titlePage = null,
        ThemeMusic? themeMusic = null,
        IUiDispatcher? dispatcher = null,
        TimeProvider? time = null,
        RatingWrites? ratings = null,
        SeriesAirings? airings = null,
        EpisodeSpans? spans = null,
        BoxSetsApi? boxSets = null,
        BoxSetLibraries? boxSetLibraries = null)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _artwork = artwork;
        _logger = logger;
        _changes = changes;
        _trackPreferences = trackPreferences;
        _trackChoices = trackChoices;
        _spoilers = spoilers;
        _titlePage = titlePage;
        _themeMusic = themeMusic;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _ratings = ratings;
        _airings = airings;
        _spans = spans;
        _boxSets = boxSets;
        _boxSetLibraries = boxSetLibraries;
    }

    /// <summary>The display scale artwork is requested for (1.5 at 150 %); set it before loading.</summary>
    public double DisplayScale { get; set; } = 1;

    public string ItemId { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsNotFound), nameof(HasLoadError))]
    public partial ItemDetailState State { get; private set; } = ItemDetailState.Loading;

    public bool IsLoading => State == ItemDetailState.Loading;

    public bool IsReady => State == ItemDetailState.Ready;

    public bool IsNotFound => State == ItemDetailState.NotFound;

    public bool HasLoadError => State == ItemDetailState.Failed;

    [ObservableProperty]
    public partial string LoadErrorText { get; private set; } = "";

    public bool CanGoBack => _navigation.CanGoBack;

    // Hero

    /// <summary>The item type (open set, <see cref="CatalogItemTypes"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedLabel))]
    public partial string Type { get; private set; } = "";

    /// <summary>The item's own title, also the page's heading for screen readers when a logo stands in for it.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string DisplayTitle { get; private set; } = "";

    // Texts the hero may leave out are empty rather than null: x:Bind skips a function binding whose
    // argument is null, so a null would never hide its line.

    [ObservableProperty]
    public partial string Tagline { get; private set; } = "";

    /// <summary>A season's own name under the series title ("Staffel 3"), or empty.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? BackdropUrl { get; private set; }

    /// <summary>The backdrop's placeholder, shown blurred until the backdrop itself is in.</summary>
    [ObservableProperty]
    public partial string BackdropThumbhash { get; private set; } = "";

    /// <summary>The logo the hero names the title with; none when the profile turned title art off (ItemDetailViewModel.TitlePage.cs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLogo), nameof(ShowsTitleText))]
    public partial Uri? LogoUrl { get; private set; }

    public bool ShowsLogo => LogoUrl is not null && !_logoFailed;

    public bool ShowsTitleText => !ShowsLogo && !HoldsTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBreadcrumbs))]
    public partial IReadOnlyList<DetailCrumb> Breadcrumbs { get; private set; } = [];

    public bool HasBreadcrumbs => Breadcrumbs.Count > 0;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverview), nameof(ShowsOverview))]
    public partial string Overview { get; private set; } = "";

    public bool HasOverview => Overview.Length > 0;

    /// <summary>The hero shows the overview: there is one, and spoiler protection does not hide it (<see cref="IsOverviewHidden"/>).</summary>
    public bool ShowsOverview => HasOverview && !IsOverviewHidden;




    /// <summary>The one line under the title: "2026 · 1h 58m · R · Mystery, Thriller".</summary>
    [ObservableProperty]
    public partial string MetaLine { get; private set; } = "";

    /// <summary>
    /// "Available version: 720P · H264 · EAC3 · 9.3 GB" under the buttons; empty for a series or season, and for several
    /// files, which the Version picker names instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableVersionLine))]
    public partial string AvailableVersionLine { get; private set; } = "";

    public bool HasAvailableVersionLine => AvailableVersionLine.Length > 0;

    /// <summary>What the Info dialog lists: details, cast, crew and the files.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailInfoSection> InfoSections { get; private set; } = [];

    // Actions

    /// <summary>The primary button's words: "Resume from 1:02:33" for a movie or episode, else <see cref="PlayLabel"/>.</summary>
    [ObservableProperty]
    public partial string PlayButtonText { get; private set; } = "Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOver))]
    public partial string PlayLabel { get; private set; } = "Play";

    /// <summary>What Play starts: the item itself, or for a series or season the episode to resume or play next.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StartOverCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartOver))]
    public partial string? PlayTargetId { get; private set; }

    /// <summary>Play resumes a title in progress; "Start over" plays it from the beginning instead.</summary>
    public bool CanStartOver => PlayTargetId is not null && PlayLabel == DetailFacts.ResumeLabel;

    [ObservableProperty]
    public partial bool ShowsWatchedAction { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedLabel))]
    public partial bool IsWatched { get; private set; }

    public string WatchedLabel => DetailFacts.WatchedLabel(Type, IsWatched);

    /// <summary>The title's catalog content id, as a collection keeps it.</summary>
    public string ContentId => _contentId;

    /// <summary>Movies and series have a favourite heart and a watchlist entry, as on the web.</summary>
    [ObservableProperty]
    public partial bool CanFavorite { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; private set; }

    public string FavoriteLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistLabel))]
    public partial bool IsInWatchlist { get; private set; }

    public string WatchlistLabel => IsInWatchlist ? "Remove from Watchlist" : "Add to Watchlist";

    /// <summary>Why the last watched, favourite, watchlist or rating change did not stick; empty when it did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string ActionError { get; private set; } = "";

    public bool HasActionError => ActionError.Length > 0;

    // Sections

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeasons))]
    public partial IReadOnlyList<MediaCard> Seasons { get; private set; } = [];

    public bool HasSeasons => Seasons.Count > 0;

    [ObservableProperty]
    public partial string SeasonsTotalText { get; private set; } = "";

    /// <summary>The season's episodes; also a single-season series', which lists them instead of one season card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEpisodes), nameof(ShowsEpisodesSection))]
    public partial IReadOnlyList<DetailEpisode> Episodes { get; private set; } = [];

    public bool HasEpisodes => Episodes.Count > 0;

    /// <summary>The episode list arrived and is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEpisodesSection))]
    public partial bool ShowsNoEpisodes { get; private set; }

    public bool ShowsEpisodesSection => HasEpisodes || ShowsNoEpisodes;

    [ObservableProperty]
    public partial string EpisodesTotalText { get; private set; } = "";

    /// <summary>
    /// An episode's whole season in its order, the episode itself marked (<see cref="MediaCard.IsCurrent"/>), as the web's;
    /// empty unless there is another episode to go to.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreEpisodes))]
    public partial IReadOnlyList<MediaCard> MoreEpisodes { get; private set; } = [];

    public bool HasMoreEpisodes => MoreEpisodes.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrailers))]
    public partial IReadOnlyList<DetailTrailer> Trailers { get; private set; } = [];

    public bool HasTrailers => Trailers.Count > 0;

    /// <summary>The Cast &amp; Crew row as laid out: the cards in their groups, a separator before each after the first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(People), nameof(HasPeople))]
    public partial IReadOnlyList<DetailCredit> Credits { get; private set; } = [];

    /// <summary>The row's cards without its separators.</summary>
    public IReadOnlyList<DetailPerson> People => [.. Credits.OfType<DetailPerson>()];

    // A separator stands only before cards: a row with entries has people.
    public bool HasPeople => Credits.Count > 0;

    [ObservableProperty]
    public partial string PeopleTitle { get; private set; } = "Cast";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSimilar))]
    public partial IReadOnlyList<MediaCard> Similar { get; private set; } = [];

    public bool HasSimilar => Similar.Count > 0;

    /// <summary>The logo failed to load (for example an expired signature): the title text takes its place.</summary>
    public void OnLogoFailed()
    {
        _logoFailed = true;
        OnPropertyChanged(nameof(ShowsLogo));
        OnPropertyChanged(nameof(ShowsTitleText));
    }

    public void DismissActionError() => ActionError = "";

    /// <summary>Loads the item with this catalog content id; executing it again cancels the running load.</summary>
    [RelayCommand]
    private async Task LoadAsync(string? id, CancellationToken cancellationToken)
    {
        int generation = ++_generation;
        Reset(id);
        if (string.IsNullOrWhiteSpace(id) || id.Trim() is "." or "..")
        {
            State = ItemDetailState.NotFound;
            return;
        }
        if (_session.ActiveServer is not { } server)
        {
            LoadErrorText = "No server is selected.";
            State = ItemDetailState.Failed;
            return;
        }
        // The profile the item is read for: a switch during the read must not bind this page's writes to the next one. And
        // where the rating writes stand, to see one that ends during the read.
        string? profileId = _session.ProfileId;
        long ratingMark = _ratings?.Mark ?? 0;
        try
        {
            var images = await ImageParametersAsync(server, cancellationToken);
            _imageParameters = images;
            var item = await _catalog.GetItemAsync(server.BaseUri, id, images: images, cancellationToken: cancellationToken);
            if (generation != _generation)
            {
                return;
            }
            Show(item, server.BaseUri, profileId, ratingMark);
            State = ItemDetailState.Ready;
            await LoadSectionsAsync(item, server.BaseUri, profileId, images, generation, cancellationToken);
            if (generation == _generation)
            {
                // A stop or a mark that came while the page loaded may be newer than what it read.
                _isLoaded = true;
                RefreshIfStale();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page was left or the load restarted.
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            if (generation != _generation)
            {
                return;
            }
            if (ex is SiloApiException { Status: 404 })
            {
                State = ItemDetailState.NotFound;
                return;
            }
            _logger.LogWarning("Loading item {ItemId} failed: {Failure}", id, ApiFailureText.Describe(ex));
            LoadErrorText = DescribeLoadFailure(ex);
            State = ItemDetailState.Failed;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadCommand.ExecuteAsync(ItemId);

    // Unreadable image capabilities only cost the sizing: the server's default sizes still work.
    private async Task<ImageParameters> ImageParametersAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        double scale = double.IsFinite(DisplayScale) && DisplayScale > 0 ? DisplayScale : 1;
        try
        {
            return await _images.GetAsync(server, ImageSizes.Default.ScaledBy(scale), cancellationToken);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Reading the image sizes failed, using the server's defaults: {Failure}", ApiFailureText.Describe(ex));
            return ImageParameters.None;
        }
    }

    /// <summary>Plays the title, or resumes it, with the version, audio and subtitles picked under the buttons.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (PlayTargetId is { } target)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(target, ChoiceFor(target)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartOver))]
    private void StartOver()
    {
        if (PlayTargetId is { } target)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.FromStart(target, ChoiceFor(target)));
        }
    }

    private bool CanPlay() => PlayTargetId is not null;

    private void Reset(string? id)
    {
        ItemId = id ?? "";
        _item = null;
        _contentId = "";
        _serverBase = null;
        _watch = null;
        _seasons = [];
        _episodes = [];
        _episodesSeasonId = null;
        _siblings = [];
        _listsEpisodes = false;
        _logoFailed = false;
        // What changed before this load is in what it reads.
        _isLoaded = false;
        _watchStateStale = false;
        State = ItemDetailState.Loading;
        LoadErrorText = "";
        ActionError = "";
        Breadcrumbs = [];
        Overview = "";
        IsOverviewHidden = false;
        ResetTitlePage();
        BackdropUrl = null;
        BackdropThumbhash = "";
        AvailableVersionLine = "";
        ResetPrePlay();
        ResetRating();
        InfoSections = [];
        LogoUrl = null;
        Seasons = [];
        Episodes = [];
        StopPlacing();
        _places = [];
        EpisodeGrid.Clear();
        ShowsNoEpisodes = false;
        MoreEpisodes = [];
        ExtraGroups = [];
        Trailers = [];
        Credits = [];
        Similar = [];
        ResetBoxSet();
    }

    private void Show(CatalogItemDetail item, Uri server, string? profileId, long ratingMark)
    {
        _item = item;
        _contentId = string.IsNullOrWhiteSpace(item.ContentId) ? ItemId.Trim() : item.ContentId;
        _serverBase = server;
        _watch = item.UserData;
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        Type = item.Type;
        Title = item.Title;
        DisplayTitle = DetailFacts.DisplayTitle(item);
        Tagline = item.Tagline?.Trim() ?? "";
        Subtitle = DetailFacts.Subtitle(item) ?? "";
        BackdropUrl = ArtworkUrl.Resolve(server, item.BackdropUrl);
        BackdropThumbhash = item.BackdropThumbhash ?? "";
        // An episode's logo would be its series': the episode's own title stays visible instead.
        _titleLogo = isEpisode ? null : ArtworkUrl.Resolve(server, item.LogoUrl);
        ShowTitleArt();
        Breadcrumbs = DetailFacts.Breadcrumbs(item);
        Overview = item.Overview?.Trim() ?? "";
        ShowMetaLine();
        AvailableVersionLine = DetailFacts.AvailableVersionLine(item) ?? "";
        ShowPrePlay(item);
        InfoSections = DetailFacts.InfoSections(item);
        Credits = DetailCredits.Row(DetailCredits.Groups(item, server));
        PeopleTitle = DetailCredits.PeopleTitle(People);
        ExtraGroups = DetailExtraGroup.From(item.Extras, server);
        Trailers = DetailTrailer.From(item.Videos);
        ShowsWatchedAction = DetailFacts.IsVideo(item.Type);
        CanFavorite = item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;
        IsWatched = item.UserData?.Played ?? item.UserState?.Played ?? false;
        IsFavorite = item.UserState?.IsFavorite ?? false;
        IsInWatchlist = item.UserState?.InWatchlist ?? false;
        ShowRating(item, server, profileId, ratingMark);
        ShowPlayAction();
        ShowOverview();
    }

    private Task LoadSectionsAsync(CatalogItemDetail item, Uri server, string? profileId, ImageParameters images, int generation,
        CancellationToken cancellationToken) =>
        item.Type switch
        {
            CatalogItemTypes.Series => Task.WhenAll(
                LoadSeasonsAsync(item, server, images, generation, cancellationToken),
                LoadSimilarAsync(item, server, images, generation, cancellationToken)),
            CatalogItemTypes.Movie => Task.WhenAll(
                LoadSimilarAsync(item, server, images, generation, cancellationToken),
                LoadBoxSetAsync(server, profileId, images, generation, cancellationToken)),
            CatalogItemTypes.Season => LoadSeasonEpisodesAsync(item, server, images, generation, cancellationToken),
            CatalogItemTypes.Episode => LoadEpisodeContextAsync(item, server, images, generation, cancellationToken),
            _ => Task.CompletedTask,
        };

    private async Task LoadSeasonsAsync(CatalogItemDetail item, Uri server, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        var collection = await TryLoadAsync("seasons", () => _catalog.ListSeasonsAsync(server, _contentId, images: images, cancellationToken: cancellationToken), cancellationToken);
        if (collection is null || generation != _generation)
        {
            return;
        }
        var seasons = OrderSeasons(collection.Items);
        ShowMetaLine(seasons);
        InfoSections = DetailFacts.InfoSections(item, seasons);
        if (seasons is [{ ContentId.Length: > 0 }])
        {
            // A single-season show lists its episodes right here, as on the web.
            var only = seasons[0];
            var episodes = await TryLoadAsync("episodes", () => _catalog.ListSeasonEpisodesAsync(server, only.ContentId, images: images, cancellationToken: cancellationToken), cancellationToken);
            if (generation != _generation)
            {
                return;
            }
            _seasons = seasons;
            if (episodes is not null)
            {
                ShowEpisodes(only.ContentId, episodes.Items, only.EpisodeCount);
                StartPlacing(server, _contentId, only.SeasonNumber, generation, cancellationToken);
                return;
            }
        }
        _seasons = seasons;
        ShowSeasonCards();
        if (seasons.Count > 1)
        {
            await PrefetchEpisodeStillsAsync(item, seasons, server, images, generation);
        }
    }

    // A season the user opens then shows its stills at once. The lists load one after another, the season to
    // play first, and the season page asks with the same image parameters, so its stills are the ones cached
    // here. Opening a season leaves this page: the prefetch is not cancelled with the page's load, so the
    // other seasons keep coming while the user looks at one; only loading another item stops it.
    private async Task PrefetchEpisodeStillsAsync(CatalogItemDetail item, IReadOnlyList<Season> seasons, Uri server, ImageParameters images, int generation)
    {
        int budget = MaxPrefetchedStills;
        foreach (var season in seasons.OrderBy(s => s.SeasonNumber == item.PlaySeasonNumber ? 0 : 1))
        {
            if (budget <= 0 || generation != _generation)
            {
                return;
            }
            if (season.ContentId.Length == 0)
            {
                continue;
            }
            var episodes = await TryLoadAsync("episodes to prefetch", () => _catalog.ListSeasonEpisodesAsync(server, season.ContentId, images: images), CancellationToken.None);
            if (episodes is null)
            {
                continue;
            }
            List<Uri> stills = [.. episodes.Items.Select(e => ArtworkUrl.Resolve(server, e.StillUrl)).OfType<Uri>().Take(budget)];
            budget -= stills.Count;
            _artwork.Prefetch(stills);
        }
    }

    private async Task LoadSeasonEpisodesAsync(CatalogItemDetail item, Uri server, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        var collection = await TryLoadAsync("episodes", () => _catalog.ListSeasonEpisodesAsync(server, _contentId, images: images, cancellationToken: cancellationToken), cancellationToken);
        if (collection is null || generation != _generation)
        {
            return;
        }
        ShowEpisodes(_contentId, collection.Items, item.EpisodeCount);
        ShowMetaLine(listedEpisodes: collection.Items.Count);
        InfoSections = DetailFacts.InfoSections(item, listedEpisodes: collection.Items.Count);
        ShowPlayAction();
        StartPlacing(server, item.SeriesId, item.SeasonNumber, generation, cancellationToken);
    }

    // The season crumb links once the season is known; "More Episodes" lists the whole season, this episode marked Now
    // Viewing, as the web's EpisodeContent.tsx.
    private async Task LoadEpisodeContextAsync(CatalogItemDetail item, Uri server, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.SeriesId) || item.SeasonNumber is not { } number || number < 0)
        {
            return;
        }
        string seriesId = item.SeriesId;
        var seasonsLoad = TryLoadAsync("seasons", () => _catalog.ListSeasonsAsync(server, seriesId, images: images, cancellationToken: cancellationToken), cancellationToken);
        var episodesLoad = TryLoadAsync("episodes", () => _catalog.ListEpisodesAsync(server, seriesId, number, images: images, cancellationToken: cancellationToken), cancellationToken);
        var seasons = await seasonsLoad;
        if (generation == _generation && seasons?.Items.FirstOrDefault(s => s.SeasonNumber == number) is { } season)
        {
            Breadcrumbs = DetailFacts.Breadcrumbs(item, season);
        }
        var episodes = await episodesLoad;
        if (generation == _generation && episodes is not null)
        {
            ShowSiblings(episodes.Items);
        }
    }

    private async Task LoadSimilarAsync(CatalogItemDetail item, Uri server, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        var similar = await TryLoadAsync("similar titles", () => _catalog.ListSimilarAsync(server, _contentId, SimilarLimit, images, cancellationToken), cancellationToken);
        if (similar is not null && generation == _generation)
        {
            Similar = [.. similar.Items.Where(i => i.ContentId.Length > 0 && i.ContentId != _contentId).Select(i => MediaCardFactory.Poster(i, server))];
        }
    }

    // A failed section only hides itself; the page stays.
    private async Task<T?> TryLoadAsync<T>(string section, Func<Task<T>> load, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await load();
        }
        // ArgumentException: the server named an id the client refuses to put in a path ("", "..").
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Loading the {Section} of item {ItemId} failed: {Failure}", section, ItemId, ApiFailureText.Describe(ex));
            return null;
        }
    }

    private void ShowEpisodes(string seasonId, IReadOnlyList<Episode> episodes, int? total)
    {
        _episodesSeasonId = seasonId;
        _episodes = episodes;
        _listsEpisodes = true;
        ShowEpisodeCards();
        EpisodesTotalText = $"{total ?? episodes.Count} total";
        ShowsNoEpisodes = episodes.Count == 0;
    }

    // An unwatched episode's still blurred and its overview left out as the profile chose (ItemDetailViewModel.Spoilers.cs).
    private void ShowEpisodeCards()
    {
        if (_serverBase is not { } server)
        {
            return;
        }
        var spoilers = Spoilers;
        // The overview gets three lines of its own under the card instead of the card's one-line caption.
        IReadOnlyList<DetailEpisode> episodes = [.. _episodes.Select(e => new DetailEpisode(MediaCardFactory.Episode(e, server, spoilers) with { Caption = null },
            spoilers.HidesOverview(e) || string.IsNullOrWhiteSpace(e.Overview) ? null : e.Overview.Trim()))];
        // The same cards again keep the grid as it is.
        if (!episodes.SequenceEqual(Episodes))
        {
            Episodes = episodes;
        }
        ShowEpisodeGrid();
    }

    private void ShowSeasonCards()
    {
        if (_serverBase is not { } server || _listsEpisodes)
        {
            return;
        }
        Seasons = [.. _seasons.Select(s => MediaCardFactory.Season(s, server))];
        SeasonsTotalText = $"{_seasons.Count} total";
    }

    private void ShowWatchState()
    {
        if (_listsEpisodes)
        {
            ShowEpisodeCards();
        }
        else if (_seasons.Count > 0)
        {
            ShowSeasonCards();
        }
        ShowSiblingCards();
        ShowPlayAction();
        ShowOverview();
    }

    private void ShowPlayAction()
    {
        if (_item is not { } item)
        {
            return;
        }
        var action = DetailFacts.PlayAction(item, _watch, item.Type == CatalogItemTypes.Season ? _episodes.FirstOrDefault()?.ContentId : null);
        PlayLabel = action.Label;
        PlayTargetId = action.TargetId;
        PlayButtonText = DetailFacts.PlayButtonText(item, _watch, action);
    }

    // Specials (season 0) go last, as in the Android client.
    private static IReadOnlyList<Season> OrderSeasons(IReadOnlyList<Season> seasons) =>
        [.. seasons.OrderBy(s => DetailFacts.IsSpecials(s) ? 1 : 0).ThenBy(s => s.SeasonNumber)];

    private static string DescribeLoadFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => "The server is receiving too many requests. " + ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => $"The server could not load this title (error {api.Status}).",
        _ => ApiFailureText.ForTransport(ex),
    };
}
