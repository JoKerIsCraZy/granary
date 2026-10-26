using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>A title from TMDB on a poster card, as the web's <c>RequestPosterCard</c>, and where opening it goes.</summary>
internal static class RequestCards
{
    private const string IdPrefix = "tmdb:";

    /// <summary>
    /// The card: "2024" or "2024 SERIES" under the title, and its request state, Available for a title in the library, or
    /// why it cannot be requested. Its id is "tmdb:movie:603", apart from every library id; its one action is the
    /// watchlist (<see cref="MediaCardActions.ToggleTitleWatchlistAsync"/>).
    /// </summary>
    public static MediaCard Of(RequestMediaResult result)
    {
        string year = result.Year is int y and > 0 ? y.ToString(CultureInfo.InvariantCulture) : "";
        return new MediaCard(IdOf(result), RequestsViewModel.CardType, result.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = result.MediaType == "series" ? $"{year} SERIES".Trim() : year,
            ImageUrl = TmdbImages.Poster(result.PosterPath),
            StatusBadge = Badge(result.Request, result.Availability),
            InWatchlist = result.InWatchlist,
        };
    }

    /// <summary>"APPROVED", "AVAILABLE" for a title in the library, or why it cannot be requested; null for none.</summary>
    public static string? Badge(RequestMediaState request, string? availability)
    {
        string? state = request.Status is { Length: > 0 }
            ? RequestText.DisplayState(request.Status, null, request.State)
            : availability == "available" ? RequestStates.Available : null;
        string? badge = state is not null ? RequestText.StateLabel(state)
            : !request.Requestable ? RequestText.ReasonLabel(request.Reason)
            : null;
        return badge?.ToUpperInvariant();
    }

    public static string IdOf(RequestMediaResult result) => $"{IdPrefix}{result.MediaType}:{result.TmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The media type and TMDB id a card's id names; null for a card that is not a TMDB title's.</summary>
    public static (string MediaType, long TmdbId)? TitleOf(string cardId)
    {
        if (!cardId.StartsWith(IdPrefix, StringComparison.Ordinal) || cardId[IdPrefix.Length..].Split(':') is not [var mediaType, var id])
        {
            return null;
        }
        return mediaType is "movie" or "series" && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long tmdbId) && tmdbId > 0
            ? (mediaType, tmdbId)
            : null;
    }

    /// <summary>The library's title when the library has it, else the title's own page; null for a title neither can show.</summary>
    public static (AppRoute Route, object Parameter)? Destination(RequestMediaResult result) =>
        !string.IsNullOrWhiteSpace(result.LibraryContentId) ? (AppRoute.Item, result.LibraryContentId)
        : result.TmdbId > 0 && result.MediaType is "movie" or "series" ? (AppRoute.Title, new TitleRouteParameter(result.MediaType, result.TmdbId, result.Title))
        : null;
}
