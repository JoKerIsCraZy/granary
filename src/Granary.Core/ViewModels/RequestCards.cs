using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>A title from TMDB on a poster card, as the web's <c>RequestPosterCard</c>, and where opening it goes.</summary>
internal static class RequestCards
{
    /// <summary>
    /// The card: "2024" or "2024 SERIES" under the title, and its request state, Available for a title in the library, or
    /// why it cannot be requested. Its id is "movie:603"; it has no card actions.
    /// </summary>
    public static MediaCard Of(RequestMediaResult result)
    {
        string? state = result.Request.Status is { Length: > 0 }
            ? RequestText.DisplayState(result.Request.Status, null, result.Request.State)
            : result.Availability == "available" ? RequestStates.Available : null;
        string? badge = state is not null ? RequestText.StateLabel(state)
            : !result.Request.Requestable ? RequestText.ReasonLabel(result.Request.Reason)
            : null;
        string year = result.Year is int y and > 0 ? y.ToString(CultureInfo.InvariantCulture) : "";
        return new MediaCard(IdOf(result), RequestsViewModel.CardType, result.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = result.MediaType == "series" ? $"{year} SERIES".Trim() : year,
            ImageUrl = TmdbImages.Poster(result.PosterPath),
            StatusBadge = badge?.ToUpperInvariant(),
        };
    }

    public static string IdOf(RequestMediaResult result) => $"{result.MediaType}:{result.TmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The library's title when the library has it, else the title's own page; null for a title neither can show.</summary>
    public static (AppRoute Route, object Parameter)? Destination(RequestMediaResult result) =>
        !string.IsNullOrWhiteSpace(result.LibraryContentId) ? (AppRoute.Item, result.LibraryContentId)
        : result.TmdbId > 0 && result.MediaType is "movie" or "series" ? (AppRoute.Title, new TitleRouteParameter(result.MediaType, result.TmdbId, result.Title))
        : null;
}
