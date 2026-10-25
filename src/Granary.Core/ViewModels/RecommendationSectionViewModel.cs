using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Explore all" of a For You row, as Silo's web shows it (<c>pages/RecommendationsSection.tsx</c>): the row in full
/// (<c>GET /api/v2/recommendations/section/{kind}</c>, up to 60 cards) as a poster grid under its name and count.
/// </summary>
public sealed partial class RecommendationSectionViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly RecommendationsApi _recommendations;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly ILogger<RecommendationSectionViewModel> _logger;
    private RecommendationSectionRouteParameter? _section;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private CancellationTokenSource _run = new();

    public RecommendationSectionViewModel(ISessionManager session, RecommendationsApi recommendations, ImageParameterCache images,
        INavigationService navigation, ILogger<RecommendationSectionViewModel> logger)
    {
        _session = session;
        _recommendations = recommendations;
        _images = images;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<MediaCard> Cards { get; } = [];

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"24 titles"; empty until there are cards.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>
    /// Shows a row. <paramref name="isReturn"/> (Back) keeps the grid of the same row for the same profile; otherwise the row
    /// is read again, since watching and rating change it.
    /// </summary>
    public Task ShowAsync(RecommendationSectionRouteParameter? section, bool isReturn = false)
    {
        if (section is null || !RecommendationSectionKinds.IsKnown(section.Kind))
        {
            return Task.CompletedTask;
        }
        bool current = section == _section && _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        if (isReturn && current)
        {
            return Task.CompletedTask;
        }
        _section = section;
        return LoadAsync();
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is { IsPlaceholder: false })
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    private async Task LoadAsync()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Cards.Clear();
        CountText = "";
        if (_section is not { } section || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        Title = section.Title.Length > 0 ? section.Title : FallbackTitle(section);
        _loadedFor = Key();
        State = LibraryLoadState.Loading;
        try
        {
            var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, token);
            var row = await _recommendations.GetSectionAsync(server.BaseUri, section.Kind, section.Key, images, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            if (row.Title.Length > 0)
            {
                Title = row.Title;
            }
            foreach (var item in row.Items.Where(i => !string.IsNullOrWhiteSpace(i.ContentId)).DistinctBy(i => i.ContentId, StringComparer.Ordinal))
            {
                Cards.Add(MediaCardFactory.Poster(item, server.BaseUri));
            }
            CountText = Cards.Count == 0 ? "" : CollectionCards.Titles(Cards.Count);
            State = Cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Another row took over.
                return;
            }
            if (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not load a recommendation section");
            }
            else
            {
                // Still the page's to show: a spinner that never ends would hide it.
                _logger.LogError(ex, "A recommendation section could not be shown");
            }
            State = LibraryLoadState.Error;
        }
    }

    // The web's names for a row whose answer has none.
    private static string FallbackTitle(RecommendationSectionRouteParameter section) => section.Kind switch
    {
        "for-you-main" => "For You",
        "cluster" => section.Key is { Length: > 0 } key ? $"Personalized cluster {key}" : "Personalized cluster",
        "similar-users" => "Profiles Like You Enjoyed",
        "popular" => "Popular on This Server",
        "recently-added" => "Recently Added",
        "top-rated" => "Highly Rated in Your Library",
        "genre" => section.Key is { Length: > 0 } genre ? $"Top {genre}" : "Genre picks",
        _ => "Recommendations",
    };

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
