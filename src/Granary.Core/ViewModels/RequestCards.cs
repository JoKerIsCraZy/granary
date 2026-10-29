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
    /// why it cannot be requested. Its id is "tmdb:movie:603", apart from every library id; its actions are the watchlist
    /// (<see cref="MediaCardActions.ToggleTitleWatchlistAsync"/>) and, while the viewer may request it, Request
    /// (<see cref="MediaCardActions.RequestAsync"/>).
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
            RequestOffer = OfferOf(result),
        };
    }

    /// <summary>"PENDING", "APPROVED", … for a request just made; null when the server named no state.</summary>
    public static string? Badge(MediaRequest request) =>
        RequestText.DisplayState(request.Status, request.Outcome, request.State) is { } state ? RequestText.StateLabel(state).ToUpperInvariant() : null;

    // The web's requestInputFromMediaResult: the title as TMDB gave it, a series without seasons (so all of it).
    private static MediaRequestCreate? OfferOf(RequestMediaResult result) =>
        result.Request.Requestable && result.TmdbId > 0 && result.MediaType is "movie" or "series"
            ? new MediaRequestCreate
            {
                MediaType = result.MediaType,
                TmdbId = result.TmdbId,
                Title = result.Title,
                Year = result.Year is > 0 ? result.Year : null,
                Overview = string.IsNullOrWhiteSpace(result.Overview) ? null : result.Overview,
                PosterPath = string.IsNullOrWhiteSpace(result.PosterPath) ? null : result.PosterPath,
                BackdropPath = string.IsNullOrWhiteSpace(result.BackdropPath) ? null : result.BackdropPath,
            }
            : null;

    /// <summary>"APPROVED", "AVAILABLE" for a title in the library, or why it cannot be requested; null for none.</summary>
    public static string? Badge(RequestMediaState request, string? availability) => StateText(request, availability)?.ToUpperInvariant();

    /// <summary>The badge's words as written: "Approved", "Available" for a title in the library, or why it cannot be requested.</summary>
    public static string? StateText(RequestMediaState request, string? availability)
    {
        string? state = request.Status is { Length: > 0 }
            ? RequestText.DisplayState(request.Status, null, request.State)
            : availability == "available" ? RequestStates.Available : null;
        return state is not null ? RequestText.StateLabel(state)
            : !request.Requestable ? RequestText.ReasonLabel(request.Reason)
            : null;
    }

    /// <summary>
    /// A watchlist title the library does not have, as the web's <c>WatchlistTitleCard</c>: "MOVIE · 2020" under the title, the
    /// status as the profile's Request Status badge and in words as the caption (amber while TMDB lost the title), and the watchlist toggle on, to take it
    /// off.
    /// </summary>
    public static MediaCard Of(WatchlistTitle title, WatchlistTitleStatus status)
    {
        string type = title.MediaType == "series" ? "SERIES" : "MOVIE";
        return new MediaCard(IdOf(title.MediaType, title.TmdbId), RequestsViewModel.CardType, title.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = title.Year is int year and > 0 ? $"{type} · {year.ToString(CultureInfo.InvariantCulture)}" : type,
            Caption = status.Caption,
            IsCaptionAttention = status.Attention,
            ImageUrl = TmdbImages.Poster(title.PosterPath),
            // The state is the profile's Request Status badge, beside TMDB's rating, the age rating and the year (the web's
            // overlayDataFromWatchlistTitle); the file badges have nothing to say about a title not in the library.
            Overlay = new Overlays.OverlayData
            {
                RatingTmdb = title.VoteAverage,
                ContentRating = string.IsNullOrEmpty(title.ContentRating) ? null : title.ContentRating,
                Year = title.Year is > 0 ? title.Year : null,
                RequestStatus = status.Badge,
                RequestStatusAttention = status.Attention,
            },
            InWatchlist = true,
        };
    }

    public static string IdOf(RequestMediaResult result) => IdOf(result.MediaType, result.TmdbId);

    public static string IdOf(string mediaType, long tmdbId) => $"{IdPrefix}{mediaType}:{tmdbId.ToString(CultureInfo.InvariantCulture)}";

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
