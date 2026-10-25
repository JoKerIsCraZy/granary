using System.Collections.Concurrent;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Cards;

/// <summary>The viewer's own state of a card: watched, favourite, on the watchlist.</summary>
public sealed record MediaCardState(bool IsWatched, bool IsFavorite, bool InWatchlist);

/// <summary>
/// What a card's own buttons do, as on Silo's web cards: play, mark watched, favourite and watchlist. Changes show at
/// once and go back when the server refuses them. Cards are records of what the server said, and a scrolled list
/// shows them again in recycled containers, so the changes of this session are kept here by content id until the
/// session or profile changes.
/// </summary>
public sealed class MediaCardActions
{
    private readonly CatalogApi _catalog;
    private readonly ISessionManager _session;
    private readonly INavigationService _navigation;
    private readonly ILogger<MediaCardActions> _logger;
    private readonly ViewerStateChanges? _changes;
    private readonly ConcurrentDictionary<string, MediaCardState> _changed = new(StringComparer.Ordinal);

    public MediaCardActions(CatalogApi catalog, ISessionManager session, INavigationService navigation, ILogger<MediaCardActions> logger,
        ViewerStateChanges? changes = null)
    {
        _catalog = catalog;
        _session = session;
        _navigation = navigation;
        _logger = logger;
        _changes = changes;
        // Another profile has its own state; the next pages show it as the server knows it.
        _session.Changed += (_, _) => _changed.Clear();
    }

    /// <summary>Raised with the content id whose state changed (and when a change went back).</summary>
    public event EventHandler<string>? Changed;

    public MediaCardState StateOf(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _changed.TryGetValue(card.Id, out var state) ? state : new MediaCardState(card.IsWatched, card.IsFavorite, card.InWatchlist);
    }

    public static bool CanPlay(MediaCard card) => card.PlayContentId is { Length: > 0 };

    public static bool CanMarkWatched(MediaCard card) => DetailFacts.IsVideo(card.Type);

    /// <summary>Favourites and the watchlist hold movies and series, as on the detail page.</summary>
    public static bool CanCollect(MediaCard card) => card.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;

    /// <summary>Starts what the card's Play stands for (a series' next episode), where the viewer left off.</summary>
    public void Play(MediaCard card)
    {
        if (card.PlayContentId is { Length: > 0 } contentId)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(contentId));
        }
    }

    public Task<bool> ToggleWatchedAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsWatched = !state.IsWatched },
            (server, state) => _catalog.SetWatchedAsync(server, card.Id, state.IsWatched));

    public Task<bool> ToggleFavoriteAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsFavorite = !state.IsFavorite },
            (server, state) => _catalog.SetFavoriteAsync(server, card.Id, state.IsFavorite));

    public Task<bool> ToggleWatchlistAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { InWatchlist = !state.InWatchlist },
            (server, state) => _catalog.SetWatchlistAsync(server, card.Id, state.InWatchlist));

    private async Task<bool> ChangeAsync(MediaCard card, Func<MediaCardState, MediaCardState> change, Func<Uri, MediaCardState, Task> send)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_session.ActiveServer is not { } server)
        {
            return false;
        }
        var before = StateOf(card);
        var after = change(before);
        Show(card.Id, after);
        try
        {
            await send(server.BaseUri, after);
            _changes?.Report();
            return true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing {ContentId} from its card failed: {Failure}", card.Id, ApiFailureText.Describe(ex));
            Show(card.Id, before);
            return false;
        }
    }

    private void Show(string contentId, MediaCardState state)
    {
        _changed[contentId] = state;
        Changed?.Invoke(this, contentId);
    }
}
