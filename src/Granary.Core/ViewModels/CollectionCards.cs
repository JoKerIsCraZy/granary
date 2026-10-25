using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>The Collections page's poster cards, worded as the web's <c>CollectionMetaLine</c>: "Manual · 4 titles".</summary>
internal static class CollectionCards
{
    public static MediaCard Of(LibraryCollectionCard collection, Uri server) =>
        new(collection.Id, LibraryCollectionsViewModel.CardType, collection.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = Titles(collection.ItemCount),
            ImageUrl = ArtworkUrl.Resolve(server, collection.PosterUrl),
        };

    /// <param name="isOwn">The profile's own collection, which says when other profiles see it too.</param>
    public static MediaCard Of(PersonalCollection collection, Uri server, bool isOwn)
    {
        string meta = $"{KindLabel(collection.CollectionType)} · {Titles(collection.ItemCount)}";
        // Failed or syncing lists say so; a healthy sync stays quiet.
        meta = collection.LastSyncStatus switch
        {
            PersonalCollectionTypes.SyncRunning => meta + " · Syncing now",
            PersonalCollectionTypes.SyncFailed => meta + " · Sync failed",
            _ => meta,
        };
        return new MediaCard(collection.Id, LibraryCollectionsViewModel.CardType, collection.Name)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = meta,
            ImageUrl = ArtworkUrl.Resolve(server, collection.PosterUrl),
            StatusBadge = isOwn && collection.IsShared ? "SHARED" : null,
        };
    }

    public static string Titles(int count) =>
        count == 1 ? "1 title" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} titles";

    private static string KindLabel(string collectionType) => collectionType switch
    {
        PersonalCollectionTypes.Manual => "Manual",
        PersonalCollectionTypes.Smart => "Smart",
        _ => "Synced list",
    };
}
