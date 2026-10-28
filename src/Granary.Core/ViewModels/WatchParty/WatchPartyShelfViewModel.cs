using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The room's browse shelf, as the web's (<c>room/browse/BrowseShelf.tsx</c> and <c>picker/usePickerData.ts</c>): a
/// search over movies and series, the filters All, Movies, Series, Together, Watchlists and Recent, and the rows the
/// room has in common: what two or more members are watching together and what is on anyone's watchlist (each card with
/// the members' badges), the first discovery rows of Home (personal rows left out), and the newest movies and series.
/// Choosing a card commits nothing: it goes on the stage as a candidate. The shelf folds to one line where the stage
/// should own the screen.
/// </summary>
public sealed partial class WatchPartyShelfViewModel : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan PickerFreshFor = TimeSpan.FromSeconds(30);

    public const string AllChip = "all";
    public const string MoviesChip = "movies";
    public const string SeriesChip = "series";
    public const string TogetherChip = "together";
    public const string WatchlistsChip = "watchlists";
    public const string RecentChip = "recent";

    private const int MaxHomeRows = 4;
    private const int RecentLimit = 24;
    private const int SearchLimit = 30;

    // Rows of one member's own history, and Home's own "recently added", which the shelf's rows replace.
    private static readonly HashSet<string> SkippedSections = new(StringComparer.Ordinal)
    {
        "recently_added", "recently_released", "new_to_library", "continue_watching", "next_up", "next_in_series", "watchlist", "favorites",
        "profile_activity_feed", "because_you_watched", "recommended_for_you", "similar_users_liked", "taste_match", "forgotten_favorites",
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyTexts = new Dictionary<string, string>
    {
        [AllChip] = "Nothing to browse yet. Search for something above.",
        [MoviesChip] = "No movies in the library yet. Search for one above.",
        [SeriesChip] = "No series in the library yet. Search for one above.",
        [TogetherChip] = "Nothing in progress for two or more of you yet.",
        [WatchlistsChip] = "Nobody in the room has anything on their watchlist.",
        [RecentChip] = "Nothing added recently.",
    };

    private readonly WatchPartySession _party;
    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private WatchPartyPicker? _picker;
    private string _pickerMembers = "";
    private DateTimeOffset _pickerReadAt = DateTimeOffset.MinValue;
    private IReadOnlyList<(string Title, IReadOnlyList<CatalogItem> Items)> _homeRows = [];
    private IReadOnlyList<CatalogItem> _recentMovies = [];
    private IReadOnlyList<CatalogItem> _recentSeries = [];
    private IReadOnlyList<CatalogItem> _found = [];
    private bool _browseRead;
    private bool _browseReading;
    private CancellationTokenSource? _search;

    public WatchPartyShelfViewModel(WatchPartySession party, ISessionManager session, CatalogApi catalog, ImageParameterCache images, TimeProvider time,
        ILogger logger)
    {
        _party = party;
        _session = session;
        _catalog = catalog;
        _images = images;
        _time = time;
        _logger = logger;
        SelectedChip = Chips[0];
    }

    /// <summary>A card the viewer chose, to put on the stage.</summary>
    public event EventHandler<WatchPartyShelfCard>? CardChosen;

    public IReadOnlyList<SettingChoice> Chips { get; } =
    [
        new(AllChip, "All"), new(MoviesChip, "Movies"), new(SeriesChip, "Series"), new(TogetherChip, "Together"),
        new(WatchlistsChip, "Watchlists"), new(RecentChip, "Recent"),
    ];

    /// <summary>The rows that show, under the chosen filter.</summary>
    public ObservableCollection<WatchPartyShelfRow> Rows { get; } = [];

    /// <summary>What the search found, under the chosen filter.</summary>
    public ObservableCollection<WatchPartyShelfCard> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial SettingChoice SelectedChip { get; set; }

    /// <summary>The title on the stage (a candidate, or what the lobby has up), ringed in the rows.</summary>
    [ObservableProperty]
    public partial string? SelectedId { get; set; }

    /// <summary>Folds to one line (a staged lobby for its host, a playing room) until opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded), nameof(IsFolded))]
    public partial bool IsCollapsible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded), nameof(IsFolded))]
    public partial bool IsOpen { get; set; }

    /// <summary>"Change what's up next" or "Suggest something for after".</summary>
    [ObservableProperty]
    public partial string FoldedLabel { get; set; } = "Browse";

    /// <summary>Choosing stages the title (the host of a host-pick lobby) or suggests it (everyone else).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldedHint), nameof(ShelfName))]
    public partial bool Picks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchState))]
    public partial string SearchState { get; private set; } = "";

    [ObservableProperty]
    public partial bool SearchFailed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    public partial string EmptyText { get; private set; } = "";

    public bool IsExpanded => !IsCollapsible || IsOpen;

    public bool IsFolded => !IsExpanded;

    public bool IsSearching => Query.Trim().Length > 0;

    public bool IsBrowsing => !IsSearching;

    public bool HasSearchState => SearchState.Length > 0;

    public bool HasEmptyText => EmptyText.Length > 0;

    public string FoldedHint => $"Search or browse what the room has in common to {(Picks ? "pick" : "suggest")} something.";

    public string ShelfName => Picks ? "Browse to pick" : "Browse to suggest";

    /// <summary>Reads what the shelf shows; the picker's rows again once the room's members change.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(ReadPickerAsync(force: false, cancellationToken), ReadBrowseAsync(cancellationToken));
    }

    /// <summary>
    /// The room changed: its members' rows are read again when they are others than before. Anything else leaves the
    /// rows as they are, so a row the viewer scrolled stays where it is.
    /// </summary>
    public void FollowRoom()
    {
        if (_picker is not null && MemberSet() != _pickerMembers)
        {
            _ = ReadPickerAsync(force: true, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void Choose(WatchPartyShelfCard? card)
    {
        if (card is not null)
        {
            CardChosen?.Invoke(this, card);
        }
    }

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Fold() => IsOpen = false;

    [RelayCommand]
    private Task RetrySearchAsync() => SearchNowAsync(Query.Trim(), CancellationToken.None);

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsBrowsing));
        _search?.Cancel();
        _search = new CancellationTokenSource();
        _ = SearchLaterAsync(value.Trim(), _search.Token);
    }

    partial void OnSelectedChipChanged(SettingChoice value) => Show();

    // Only the rings move.
    partial void OnSelectedIdChanged(string? value)
    {
        foreach (var card in Results.Concat(Rows.SelectMany(r => r.Cards)))
        {
            card.IsSelected = card.ContentId == value;
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            _ = RefreshAsync();
        }
    }

    private async Task SearchLaterAsync(string text, CancellationToken token)
    {
        if (text.Length == 0)
        {
            _found = [];
            SearchState = "";
            SearchFailed = false;
            Show();
            return;
        }
        try
        {
            await Task.Delay(SearchDelay, _time, token);
            await SearchNowAsync(text, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search took over.
        }
    }

    private async Task SearchNowAsync(string text, CancellationToken token)
    {
        if (text.Length == 0 || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        SearchState = "Searching…";
        SearchFailed = false;
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default, token);
            var page = await _catalog.BrowseAsync(server.BaseUri, new CatalogQuery { Q = text, Limit = SearchLimit, Images = images, SkipTotal = true }, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            _found = [.. page.Items.Where(Pickable)];
            SearchState = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "The room's search failed");
            _found = [];
            SearchState = "Could not search.";
            SearchFailed = true;
        }
        Show();
    }

    private async Task ReadPickerAsync(bool force, CancellationToken cancellationToken)
    {
        string members = MemberSet();
        if (!force && _picker is not null && members == _pickerMembers && _time.GetUtcNow() - _pickerReadAt < PickerFreshFor)
        {
            return;
        }
        try
        {
            if (await _party.GetPickerAsync(cancellationToken) is { } picker)
            {
                _picker = picker;
                _pickerMembers = members;
                _pickerReadAt = _time.GetUtcNow();
                Show();
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The room's picker rows could not be read: {Failure}", ex.GetType().Name);
        }
    }

    // Home's discovery rows and the newest movies and series: once per room page.
    private async Task ReadBrowseAsync(CancellationToken cancellationToken)
    {
        if (_browseRead || _browseReading || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        _browseReading = true;
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default, cancellationToken);
            var home = ReadHomeAsync(server.BaseUri, images, cancellationToken);
            var movies = RecentAsync(server.BaseUri, MediaScopes.Movie, images, cancellationToken);
            var series = RecentAsync(server.BaseUri, MediaScopes.Series, images, cancellationToken);
            await Task.WhenAll(home, movies, series);
            _homeRows = home.Result;
            _recentMovies = movies.Result;
            _recentSeries = series.Result;
            _browseRead = true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "The room's shelf could not be read");
        }
        finally
        {
            _browseReading = false;
        }
        Show();
    }

    private async Task<IReadOnlyList<(string Title, IReadOnlyList<CatalogItem> Items)>> ReadHomeAsync(Uri server, ImageParameters images,
        CancellationToken cancellationToken)
    {
        var sections = await _catalog.ListHomeSectionsAsync(server, images, cancellationToken);
        return
        [
            .. sections.Sections.Where(s => !SkippedSections.Contains(s.SectionType)).Take(MaxHomeRows)
                .Select(s => (s.Title, (IReadOnlyList<CatalogItem>)[.. s.Items.Where(Pickable)]))
                .Where(row => row.Item2.Count > 0),
        ];
    }

    private async Task<IReadOnlyList<CatalogItem>> RecentAsync(Uri server, string scope, ImageParameters images, CancellationToken cancellationToken)
    {
        var page = await _catalog.BrowseAsync(server, new CatalogQuery
        {
            Type = scope,
            Sort = CatalogSort.Descending(CatalogSortFields.AddedAt),
            Limit = RecentLimit,
            Images = images,
            SkipTotal = true,
        }, cancellationToken);
        return [.. page.Items.Where(Pickable)];
    }

    // The rows and results as the filter shows them.
    private void Show()
    {
        string chip = SelectedChip?.Value ?? AllChip;
        var server = _session.ActiveServer?.BaseUri;
        var room = _party.Room;
        var tints = room is null ? new Dictionary<string, int>() : WatchPartyMembers.Tints(room.Members);
        Results.Clear();
        foreach (var item in _found.Where(i => Fits(i.Type, chip)))
        {
            Results.Add(Card(item, server, tints));
        }
        var rows = new List<WatchPartyShelfRow>();
        if (chip is AllChip or TogetherChip && _picker is { } together)
        {
            AddRow(rows, "together", "Continue watching together", "in progress for 2+ of you",
                together.ContinueTogether.Select(e => Card(e, server, tints)));
        }
        if (chip is AllChip or WatchlistsChip && _picker is { } lists)
        {
            AddRow(rows, "watchlists", "On your watchlists", "anyone in the room", lists.WatchlistUnion.Select(e => Card(e, server, tints)));
        }
        if (chip is AllChip or MoviesChip or SeriesChip)
        {
            foreach (var (title, items) in _homeRows)
            {
                AddRow(rows, "home:" + title, title, "", items.Where(i => Fits(i.Type, chip)).Select(i => Card(i, server, tints)));
            }
        }
        if (chip is AllChip or MoviesChip or RecentChip)
        {
            AddRow(rows, "recent-movies", "Recently added movies", "", _recentMovies.Select(i => Card(i, server, tints)));
        }
        if (chip is AllChip or SeriesChip or RecentChip)
        {
            AddRow(rows, "recent-series", "Recently added series", "", _recentSeries.Select(i => Card(i, server, tints)));
        }
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
        bool read = _browseRead || _picker is not null;
        EmptyText = read && rows.Count == 0 ? EmptyTexts[chip] : "";
        OnPropertyChanged(nameof(ResultsHeading));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>"SEARCH RESULTS · 4".</summary>
    public string ResultsHeading => $"Search results · {Results.Count}";

    public bool HasNoResults => IsSearching && !HasSearchState && Results.Count == 0;

    public string NoResultsText => $"No matches for “{Query.Trim()}”. Try another title, or clear the filter.";

    private static void AddRow(List<WatchPartyShelfRow> rows, string key, string title, string hint, IEnumerable<WatchPartyShelfCard> cards)
    {
        var list = cards.ToList();
        if (list.Count > 0)
        {
            rows.Add(new WatchPartyShelfRow(key, title, hint, list));
        }
    }

    private static bool Fits(string type, string chip) => chip switch
    {
        MoviesChip => type == CatalogItemTypes.Movie,
        SeriesChip => type == CatalogItemTypes.Series,
        _ => true,
    };

    private static bool Pickable(CatalogItem item) => item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;

    private WatchPartyShelfCard Card(CatalogItem item, Uri? server, IReadOnlyDictionary<string, int> tints) =>
        new(item.ContentId, item.Type, item.Title, item.Year?.ToString(CultureInfo.InvariantCulture) ?? (item.Type == CatalogItemTypes.Series ? "Series" : "Movie"),
            server is null ? null : ArtworkUrl.Resolve(server, item.PosterUrl), item.PosterUrl, [], 0, "", null, null, item.Year)
        {
            IsSelected = item.ContentId == SelectedId,
        };

    // A picker card: who it is about (two badges and "+n"), how far the one furthest behind is, and a series' episode.
    private WatchPartyShelfCard Card(WatchPartyPickerEntry entry, Uri? server, IReadOnlyDictionary<string, int> tints)
    {
        var item = entry.Item;
        var badges = entry.Members.Take(2)
            .Select(m => new WatchPartyBadge(WatchPartyMembers.Initials(m.DisplayName), tints.TryGetValue(m.Key, out int tint) ? tint : 0, m.DisplayName))
            .ToList();
        var ratios = entry.Members.Where(m => m.PositionSeconds is not null && m.DurationSeconds is > 0)
            .Select(m => Math.Clamp(m.PositionSeconds!.Value / m.DurationSeconds!.Value, 0, 1)).ToList();
        string caption = entry.NextUp is { } next
            ? $"S{next.SeasonNumber} E{next.EpisodeNumber}"
            : item.Year?.ToString(CultureInfo.InvariantCulture) ?? (item.Type == CatalogItemTypes.Series ? "Series" : "Movie");
        return new WatchPartyShelfCard(item.ContentId, item.Type, item.Title, caption, server is null ? null : ArtworkUrl.Resolve(server, item.PosterUrl),
            item.PosterUrl, badges, Math.Max(0, entry.Members.Count - 2), string.Join(", ", entry.Members.Select(m => m.DisplayName)),
            ratios.Count > 0 ? ratios.Min() : null, entry.NextUp?.SeasonNumber, item.Year)
        {
            IsSelected = item.ContentId == SelectedId,
        };
    }

    private string MemberSet() =>
        string.Join(",", (_party.Room?.Members ?? []).Select(m => m.Key).Order(StringComparer.Ordinal));
}
