using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Texts;
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
        // The web's CollectionMetaLine: its parts after each other, with a dot between.
        string meta = $"{KindLabel(collection.CollectionType)} · {Titles(collection.ItemCount)}";
        // Failed or syncing lists say so; a healthy sync stays quiet.
        meta = collection.LastSyncStatus switch
        {
            PersonalCollectionTypes.SyncRunning => $"{meta} · {AppText.Get("web.collections.list.syncingNow")}",
            PersonalCollectionTypes.SyncFailed => $"{meta} · {AppText.Get("web.collections.list.syncFailed")}",
            _ => meta,
        };
        return new MediaCard(collection.Id, LibraryCollectionsViewModel.CardType, collection.Name)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = meta,
            ImageUrl = ArtworkUrl.Resolve(server, collection.PosterUrl),
            // The web's Shared tag, in capitals as cards' badges show them.
            StatusBadge = isOwn && collection.IsShared ? AppText.Upper(AppText.Get("web.collections.tags.shared")) : null,
        };
    }

    public static string Titles(int count) => AppText.Get("web.collections.metaLine.titleCount", ("count", count), ("shown", count));

    private static string KindLabel(string collectionType) => collectionType switch
    {
        PersonalCollectionTypes.Manual => AppText.Get("web.collections.types.kindManual"),
        PersonalCollectionTypes.Smart => AppText.Get("web.collections.types.kindSmart"),
        _ => AppText.Get("web.collections.types.kindSynced"),
    };
}
