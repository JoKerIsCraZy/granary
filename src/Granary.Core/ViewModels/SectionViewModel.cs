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
/// "Explore all" of a Home or library row: the row's cards in the row's own order, cursor-paged while
/// the grid scrolls (docs/api-contract.md, 7.1). A section listing takes no sort or filter, so there are none.
/// </summary>
/// <remarks>The commands take no cancellation token: a cancellable toolkit command cancels its running call when executed again.</remarks>
public sealed partial class SectionViewModel(
    ISessionManager session,
    CatalogApi catalog,
    ImageParameterCache images,
    INavigationService navigation,
    ILogger<SectionViewModel> logger) : ObservableObject
{
    public const int PageSize = 60;

    internal const string EmptyText = "Nothing here yet";
    internal const string GoneText = "This row is no longer available.";

    private CancellationTokenSource? _run;
    private BrowseCardPager? _listing;
    private SectionRouteParameter? _section;
    private (string? ServerId, string? ProfileId) _loadedFor;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial MediaCardShape CardShape { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    /// <summary>Page one is on its way.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A later page is on its way.</summary>
    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>A page could not be loaded; the page offers <see cref="RetryCommand"/>.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Loading, empty or the error, for the page and for screen readers.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>The row shown, or null before the first <see cref="OpenAsync"/>.</summary>
    public SectionRouteParameter? Section => _section;

    /// <summary>The current load, for tests.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows <paramref name="section"/>. The listing already shown stays as it is (Back keeps its pages
    /// and scroll position) unless it is another row, another session, or <paramref name="reload"/> is set.
    /// </summary>
    public Task OpenAsync(SectionRouteParameter section, bool reload = false)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!reload && section == _section && _loadedFor == SessionKey() && (_listing is { IsLoaded: true } || !Loading.IsCompleted))
        {
            return Loading;
        }
        _section = section;
        Title = section.Title;
        CardShape = HomeRowRules.IsWatchingRow(section.SectionType) ? MediaCardShape.Landscape : MediaCardShape.Poster;
        return Loading = LoadFirstPageAsync(section);
    }

    /// <summary>For the grid while it scrolls; after a failure only <see cref="RetryCommand"/> asks again.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => HasError ? Task.CompletedTask : LoadNextPageAsync();

    private async Task LoadNextPageAsync()
    {
        if (_listing is not { HasMore: true } listing || _run is not { } run || IsLoading)
        {
            return;
        }
        IsLoadingMore = true;
        try
        {
            await listing.LoadNextPageAsync(run.Token);
            if (listing == _listing)
            {
                ShowLoaded(listing);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            if (!run.IsCancellationRequested && listing == _listing)
            {
                ShowFailure(ex);
            }
        }
        finally
        {
            if (listing == _listing)
            {
                IsLoadingMore = false;
            }
        }
    }

    /// <summary>Loads page one again, or the page after the shown ones when only a later page failed.</summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        if (_section is not { } section)
        {
            return Task.CompletedTask;
        }
        return _listing is { IsLoaded: true } && _loadedFor == SessionKey() ? LoadNextPageAsync() : Loading = LoadFirstPageAsync(section);
    }

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    private async Task LoadFirstPageAsync(SectionRouteParameter section)
    {
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        var cancellationToken = run.Token;
        _listing = null;
        _loadedFor = SessionKey();
        Cards = [];
        IsLoadingMore = false;
        if (session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            IsLoading = false;
            ShowState(hasError: false, isEmpty: false, "");
            return;
        }
        if (string.IsNullOrWhiteSpace(section.SectionId) || (section.LibraryId is { } library && string.IsNullOrWhiteSpace(library)))
        {
            IsLoading = false;
            ShowState(hasError: true, isEmpty: false, GoneText);
            return;
        }

        IsLoading = true;
        ShowState(hasError: false, isEmpty: false, $"Loading {section.Title}");
        try
        {
            var imageParameters = await BrowseArtwork.ParametersAsync(images, server, DisplayScale, logger, cancellationToken);
            var query = section.LibraryId is { } libraryId
                ? CatalogQuery.ForLibrarySection(libraryId, section.SectionId)
                : CatalogQuery.ForHomeSection(section.SectionId);
            var shape = CardShape;
            var listing = new BrowseCardPager(catalog, server.BaseUri, query with { Limit = PageSize, Images = imageParameters },
                item => item.ContentId.Length > 0 && HomeRowRules.Shows(section.SectionType, item) ? CardFor(item, shape, server.BaseUri) : null);
            _listing = listing;
            Cards = listing.Cards;
            await listing.LoadFirstPageAsync(cancellationToken);
            ShowLoaded(listing);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            // A newer load replaced this one; its failure is stale.
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowFailure(ex);
            }
        }
        finally
        {
            if (run == _run)
            {
                IsLoading = false;
            }
        }
    }

    private static MediaCard CardFor(CatalogItem item, MediaCardShape shape, Uri server) =>
        shape == MediaCardShape.Landscape ? MediaCardFactory.Landscape(item, server) : MediaCardFactory.Poster(item, server);

    private void ShowLoaded(BrowseCardPager listing)
    {
        bool isEmpty = listing.Cards.Count == 0;
        ShowState(hasError: false, isEmpty, isEmpty ? EmptyText : "");
    }

    private void ShowFailure(Exception ex)
    {
        logger.LogWarning("Loading a row's listing failed: {Failure}", ApiFailureText.Describe(ex));
        ShowState(hasError: true, isEmpty: false, BrowseFailureText.Describe(ex, "This row could not be loaded right now.", GoneText));
    }

    private void ShowState(bool hasError, bool isEmpty, string status)
    {
        HasError = hasError;
        IsEmpty = isEmpty;
        StatusText = status;
    }

    private (string? ServerId, string? ProfileId) SessionKey() => (session.ActiveServer?.Id, session.ProfileId);
}
