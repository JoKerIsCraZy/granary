using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A For You row: its cards, and "Explore all" when a section lists the row in full.</summary>
/// <param name="OpenCardCommand">Takes the card's <see cref="MediaCard"/>.</param>
/// <param name="Section">The section Explore all opens; null when none lists the row.</param>
public sealed record RecommendationRowViewModel(string Title, IReadOnlyList<MediaCard> Cards, ICommand OpenCardCommand, ICommand? ExploreAllCommand,
    RecommendationSectionRouteParameter? Section = null)
{
    /// <summary>Whether <paramref name="other"/> shows the same: the rows on screen can stay, and keep where they are scrolled to.</summary>
    internal bool ShowsTheSameAs(RecommendationRowViewModel other) =>
        Title == other.Title && Section == other.Section && Cards.Select(c => c.Id).SequenceEqual(other.Cards.Select(c => c.Id), StringComparer.Ordinal);
}

/// <summary>
/// For You, as Silo's web shows it (<c>pages/Recommendations.tsx</c>): the profile's recommendation rows
/// (<c>GET /api/v2/recommendations/discover</c>) and, beside the title, its taste profile (top genres and favourite
/// directors). The subtitle says the rows are personal only when one comes from the profile's own history. A visit
/// from the sidebar reads the rows again and shows them once they are there; Back keeps the page as it was.
/// </summary>
public sealed partial class ForYouViewModel : ObservableObject
{
    internal const string PersonalSubtitle = "Personalized picks based on your viewing history and ratings.";
    internal const string DefaultSubtitle =
        "Highly rated and recently added titles from your library. Watch and rate titles to personalize this page.";

    private readonly ISessionManager _session;
    private readonly RecommendationsApi _recommendations;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly ILogger<ForYouViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private CancellationTokenSource _run = new();

    public ForYouViewModel(ISessionManager session, RecommendationsApi recommendations, ImageParameterCache images,
        INavigationService navigation, ILogger<ForYouViewModel> logger)
    {
        _session = session;
        _recommendations = recommendations;
        _images = images;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<RecommendationRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>Empty until rows show.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopGenres), nameof(HasTasteProfile))]
    public partial IReadOnlyList<string> TopGenres { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavoriteDirectors), nameof(HasTasteProfile))]
    public partial IReadOnlyList<string> FavoriteDirectors { get; private set; } = [];

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    public bool HasTopGenres => TopGenres.Count > 0;

    public bool HasFavoriteDirectors => FavoriteDirectors.Count > 0;

    public bool HasTasteProfile => HasTopGenres || HasFavoriteDirectors;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>
    /// Shows the page: <paramref name="isReturn"/> (Back) keeps what it shows for the same profile; otherwise the rows are
    /// read again, quietly behind the ones shown.
    /// </summary>
    public Task ActivateAsync(bool isReturn)
    {
        bool shown = _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        if (isReturn && shown)
        {
            return Task.CompletedTask;
        }
        return LoadAsync(quiet: shown && State != LibraryLoadState.Loading);
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(quiet: false);

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is { IsPlaceholder: false })
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    private async Task LoadAsync(bool quiet)
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            Show([], null);
            State = LibraryLoadState.Idle;
            return;
        }
        if (!quiet || _loadedFor != Key())
        {
            Show([], null);
            State = LibraryLoadState.Loading;
        }
        _loadedFor = Key();
        var taste = TasteAsync(server.BaseUri, token);
        try
        {
            var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, token);
            var discover = await _recommendations.GetDiscoverAsync(server.BaseUri, images, token);
            var (profile, tasteFailed) = await taste;
            if (token.IsCancellationRequested)
            {
                return;
            }
            var rows = RowsOf(discover, server.BaseUri);
            ShowRows(rows);
            // A quiet reload whose taste profile failed keeps the one shown.
            if (!tasteFailed || State == LibraryLoadState.Loading)
            {
                ShowTaste(profile);
            }
            Subtitle = rows.Count == 0 ? ""
                : discover.Items.Any(r => RecommendationRowTypes.IsPersonal(r.Type)) ? PersonalSubtitle : DefaultSubtitle;
            State = rows.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Another load took over.
                return;
            }
            if (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not load the recommendations");
            }
            else
            {
                // Still the page's to show: a spinner that never ends would hide it.
                _logger.LogError(ex, "The recommendations could not be shown");
            }
            if (State != LibraryLoadState.Loading)
            {
                // A quiet reload that failed keeps the rows it would have replaced.
                return;
            }
            State = LibraryLoadState.Error;
        }
    }

    // The taste profile is only beside the title: without it the page shows the rows alone. It never fails the page.
    private async Task<(TasteProfile? Profile, bool Failed)> TasteAsync(Uri server, CancellationToken token)
    {
        try
        {
            return (await _recommendations.GetTasteProfileAsync(server, token), false);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not load the taste profile");
            }
            return (null, true);
        }
    }

    private List<RecommendationRowViewModel> RowsOf(RecommendationRowCollection discover, Uri server)
    {
        var rows = new List<RecommendationRowViewModel>();
        foreach (var row in discover.Items)
        {
            var cards = row.Items.Where(i => !string.IsNullOrWhiteSpace(i.ContentId))
                .DistinctBy(i => i.ContentId, StringComparer.Ordinal)
                .Select(i => MediaCardFactory.Poster(i, server))
                .ToList();
            if (cards.Count == 0)
            {
                continue;
            }
            var section = RecommendationSectionKinds.IsKnown(row.Kind)
                ? new RecommendationSectionRouteParameter(row.Kind!, row.Key, row.Title)
                : null;
            var exploreAll = section is null ? null : new RelayCommand(() => _navigation.NavigateTo(AppRoute.RecommendationSection, section));
            rows.Add(new RecommendationRowViewModel(row.Title, cards, OpenCardCommand, exploreAll, section));
        }
        return rows;
    }

    private void Show(IReadOnlyList<RecommendationRowViewModel> rows, TasteProfile? taste)
    {
        ShowRows(rows);
        ShowTaste(taste);
        if (rows.Count == 0)
        {
            Subtitle = "";
        }
    }

    // The same rows again stay as they are, scrolled where the viewer left them; others replace them.
    private void ShowRows(IReadOnlyList<RecommendationRowViewModel> rows)
    {
        if (rows.Count == Rows.Count && rows.Zip(Rows).All(pair => pair.First.ShowsTheSameAs(pair.Second)))
        {
            return;
        }
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    private void ShowTaste(TasteProfile? taste)
    {
        TopGenres = taste?.TopGenres ?? [];
        FavoriteDirectors = taste?.FavoriteDirectors ?? [];
    }

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
