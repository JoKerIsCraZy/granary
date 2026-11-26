using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The collections a movie or series is in (docs/api-contract.md, 7.18a), as the web's components/collections/ItemCollectionsRow.tsx
// shows them after the box set: when one collection holds the title, up to six of its other titles under the collection's name,
// Explore all opening it; otherwise an "Also in" row with a card for each collection. As Silo's apps do (silo-android cb2c32d8a,
// silo-apple fd83eb46), only a movie or series asks, and only a server whose search capabilities say item_collections. Nothing
// shows while it loads, when no collection holds the title, or when the first read fails; a failed reread, after Add to a
// collection, keeps the row it shows.
public sealed partial class ItemDetailViewModel
{
    /// <summary>The most of a single collection's other titles the row shows, as the web and the apps.</summary>
    public const int CollectionPreviewLimit = 6;

    // The collection cards of an "Also in" row and the collection each opens, found by the card itself, the instance the row
    // shows: two cards can be equal (a collection listed for two libraries) yet open different places.
    private List<(MediaCard Card, CollectionRouteParameter Route)> _collectionRoutes = [];
    private CollectionRouteParameter? _previewCollection;
    // Counts the row's reads: an older one that answers late does not put its answer over a newer one's.
    private int _collectionReads;
    private string? _collectionsProfile;

    /// <summary>The row's title: the one collection's name over its titles, else "Also in"; empty without a row.</summary>
    [ObservableProperty]
    public partial string CollectionsRowTitle { get; private set; } = "";

    /// <summary>The one collection's other titles (<see cref="IsCollectionPreview"/>), or a card for each collection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionsRow))]
    public partial IReadOnlyList<MediaCard> CollectionsRow { get; private set; } = [];

    public bool HasCollectionsRow => CollectionsRow.Count > 0;

    /// <summary>The row shows one collection's titles rather than collection cards.</summary>
    [ObservableProperty]
    public partial bool IsCollectionPreview { get; private set; }

    /// <summary>The row's Explore all, which opens the one collection; null on an "Also in" row, which has none.</summary>
    [ObservableProperty]
    public partial ICommand? CollectionsExploreAll { get; private set; }

    /// <summary>A card of the row: a title opens its page, a collection its titles.</summary>
    [RelayCommand]
    private void OpenCollectionsRowCard(MediaCard? card)
    {
        if (card is null)
        {
            return;
        }
        if (IsCollectionPreview)
        {
            // A collection's titles can be in other libraries: the title opens without one, as the apps open them.
            Open(card.Id);
            return;
        }
        // A collection card never opens a title page: one the row does not know opens nothing.
        foreach (var (shown, route) in _collectionRoutes)
        {
            if (ReferenceEquals(shown, card))
            {
                _navigation.NavigateTo(AppRoute.Collection, route);
                return;
            }
        }
    }

    [RelayCommand]
    private void OpenCollectionPreview()
    {
        if (_previewCollection is { } route)
        {
            _navigation.NavigateTo(AppRoute.Collection, route);
        }
    }

    /// <summary>
    /// Reads the row again, as the apps and the web do once the title was added to a collection or taken out of one. A failed
    /// read keeps the row as it is.
    /// </summary>
    public async Task RefreshCollectionsAsync()
    {
        if (_item?.Type is not (CatalogItemTypes.Movie or CatalogItemTypes.Series) || _serverBase is not { } server)
        {
            return;
        }
        await LoadCollectionsAsync(server, _collectionsProfile, _imageParameters, _generation, CancellationToken.None);
    }

    // The server and profile are the ones the page was loaded for, not whichever acts by the time the answer came.
    private async Task LoadCollectionsAsync(Uri server, string? profileId, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        if (_itemCollections is null)
        {
            return;
        }
        _collectionsProfile = profileId;
        int read = ++_collectionReads;
        string contentId = _contentId;
        CollectionsAnswer? answer;
        try
        {
            answer = await ReadCollectionsAsync(server, contentId, profileId, images, cancellationToken);
        }
        // ArgumentException: the server named an id the client refuses to put in a path ("", "..").
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Loading the collections of item {ItemId} failed: {Failure}", ItemId, ApiFailureText.Describe(ex));
            }
            return;
        }
        if (generation != _generation || read != _collectionReads)
        {
            return;
        }
        ShowCollections(answer, server, profileId);
    }

    // Null when the server has no such read: then there is no row.
    private async Task<CollectionsAnswer?> ReadCollectionsAsync(Uri server, string contentId, string? profileId, ImageParameters images,
        CancellationToken cancellationToken)
    {
        var capabilities = await _catalog.GetSearchCapabilitiesAsync(server, cancellationToken);
        if (!capabilities.ItemCollections)
        {
            return null;
        }
        var collections = await _itemCollections!.ListAsync(server, contentId, images: images, cancellationToken: cancellationToken);
        // A card without an id could not be opened.
        IReadOnlyList<SectionCollectionCard> cards = [.. collections.Items.Where(c => !string.IsNullOrWhiteSpace(c.Id))];
        var owners = await OwnerNamesAsync(server, cards, profileId, cancellationToken);
        return new CollectionsAnswer(cards, collections.PreviewItems, owners);
    }

    // The names of the profiles whose shared collections hold the title, read only when there is one; unreadable, they are
    // "Another profile", as the web's ownerName.
    private async Task<IReadOnlyDictionary<string, string>> OwnerNamesAsync(Uri server, IReadOnlyList<SectionCollectionCard> cards, string? profileId,
        CancellationToken cancellationToken)
    {
        if (_profiles is null || !cards.Any(c => OwnerOf(c, profileId) is not null))
        {
            return new Dictionary<string, string>();
        }
        try
        {
            var profiles = await _profiles.ListAsync(server, cancellationToken);
            return profiles.Items.Where(p => p.Id.Length > 0).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Reading the profiles' names for the collections row failed: {Failure}", ApiFailureText.Describe(ex));
            return new Dictionary<string, string>();
        }
    }

    private void ShowCollections(CollectionsAnswer? answer, Uri server, string? profileId)
    {
        if (answer is not { Cards.Count: > 0 } found)
        {
            ResetCollections();
            return;
        }
        List<CatalogItem> others = [.. found.Preview.Where(i => i.ContentId.Length > 0 && i.ContentId != _contentId)];
        if (found.Cards is [var only] && others.Count > 0)
        {
            _collectionRoutes = [];
            _previewCollection = RouteOf(only);
            CollectionsRowTitle = only.Title;
            CollectionsRow = [.. others.Take(CollectionPreviewLimit).Select(i => MediaCardFactory.Poster(i, server))];
            IsCollectionPreview = true;
            CollectionsExploreAll = OpenCollectionPreviewCommand;
            return;
        }
        _collectionRoutes = [.. found.Cards.Select(c => (CardOf(c, server, profileId, found.Owners), RouteOf(c)))];
        _previewCollection = null;
        CollectionsRowTitle = AppText.Get("web.detail.sections.alsoIn");
        CollectionsRow = [.. _collectionRoutes.Select(c => c.Card)];
        IsCollectionPreview = false;
        CollectionsExploreAll = null;
    }

    // The web's CollectionPosterCard: the name, and under it whose a personal collection is; Granary's collection cards carry
    // the count in that line too ("by Mara · 4 titles"), as the Collections page's carry it in theirs.
    private static MediaCard CardOf(SectionCollectionCard card, Uri server, string? profileId, IReadOnlyDictionary<string, string> owners)
    {
        string titles = CollectionCards.Titles(card.ItemCount);
        string subtitle = titles;
        if (card.IsPersonal)
        {
            string byline = OwnerOf(card, profileId) is { } owner
                ? AppText.Get("web.collections.posterCard.byOwner",
                    ("owner", owners.GetValueOrDefault(owner) ?? AppText.Get("web.collections.personalOwnership.anotherProfile")))
                : AppText.Get("web.collections.posterCard.userCollection");
            subtitle = $"{byline} · {titles}";
        }
        return new MediaCard(card.Id, LibraryCollectionsViewModel.CardType, card.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = subtitle,
            // The web's line under the name is plain small text (text-xs, muted), not an overline.
            PlainSubtitle = true,
            ImageUrl = ArtworkUrl.Resolve(server, card.PosterUrl),
            ImageThumbhash = card.PosterThumbhash,
        };
    }

    // Another profile's shared collection: its owner; null for the profile's own and for a server collection.
    private static string? OwnerOf(SectionCollectionCard card, string? profileId) =>
        card.IsPersonal && !string.IsNullOrEmpty(card.CreatorProfileId) && card.CreatorProfileId != profileId ? card.CreatorProfileId : null;

    private static CollectionRouteParameter RouteOf(SectionCollectionCard card) => card.IsPersonal
        ? new CollectionRouteParameter(CatalogSources.UserCollection, card.Id, card.Title)
        : new CollectionRouteParameter(CatalogSources.LibraryCollection, card.Id, card.Title, string.IsNullOrWhiteSpace(card.LibraryId) ? null : card.LibraryId);

    private void ResetCollections()
    {
        _collectionRoutes = [];
        _previewCollection = null;
        CollectionsRowTitle = "";
        CollectionsRow = [];
        IsCollectionPreview = false;
        CollectionsExploreAll = null;
    }

    private sealed record CollectionsAnswer(IReadOnlyList<SectionCollectionCard> Cards, IReadOnlyList<CatalogItem> Preview,
        IReadOnlyDictionary<string, string> Owners);
}
