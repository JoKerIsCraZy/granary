using Granary.Core.Api.Catalog;
using Granary.Core.Overlays;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Cards;

/// <summary>A card's ratings where one stands for them all, such as a hero's line of details.</summary>
public static class CardRating
{
    private const double MaxOutOfTen = 10;

    /// <summary>
    /// The one rating a card-sized summary shows, as the web's <c>primaryCardRating</c> (components/ratings/ratings.ts): IMDb's,
    /// or TMDB's when there is no IMDb score; null without either. A score counts when it is over 0 and at most 10. It reads
    /// as the card's badge does ("IMDb", "8.4").
    /// </summary>
    public static DetailRating? Primary(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return OutOfTen("IMDb", item.RatingImdb) ?? OutOfTen("TMDB", item.RatingTmdb);
    }

    // NaN fails both comparisons, as the web's Number.isFinite leaves it out.
    private static DetailRating? OutOfTen(string name, double? score) =>
        score is > 0 and <= MaxOutOfTen and var value ? new DetailRating(name, OverlayRegistry.OutOfTen(value)) : null;
}
