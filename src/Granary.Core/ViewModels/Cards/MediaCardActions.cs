using System.Collections.Concurrent;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// The viewer's own state of a card: watched, favourite, on the watchlist, not interested, and whether this session
/// cleared where the viewer left off.
/// </summary>
/// <param name="StatusBadge">
/// A TMDB title's request state since this session put it on the watchlist and that requested it; it stands while the
/// card's own badge is still <paramref name="StatusBadgeOver"/>, the one it replaced.
/// </param>
public sealed record MediaCardState(bool IsWatched, bool IsFavorite, bool InWatchlist, bool IsNotInterested = false, bool IsProgressCleared = false,
    string? StatusBadge = null, string? StatusBadgeOver = null);

/// <summary>
/// What a card's own buttons and its menu do, as Silo's web card menu: play, play from the beginning, mark watched,
/// reset progress, favourite, watchlist and Not interested. Changes show at once and go back when the server refuses them. Cards are records of what the server said, and a scrolled list
/// shows them again in recycled containers, so the changes of this session are kept here by content id until the
/// session or profile changes. A title from TMDB (a Requests card) has one action, the web's watchlist toggle in its
/// corner, while the server keeps a watchlist of TMDB titles.
/// </summary>
public sealed class MediaCardActions
{
    private readonly CatalogApi _catalog;
    private readonly ISessionManager _session;
    private readonly INavigationService _navigation;
    private readonly ILogger<MediaCardActions> _logger;
    private readonly ViewerStateChanges? _changes;
    private readonly RequestsApi? _requests;
    private readonly RequestsFeature? _requestsFeature;
    private readonly ConcurrentDictionary<string, MediaCardState> _changed = new(StringComparer.Ordinal);
    // TMDB titles whose watchlist change is on its way: an add can request the title, so it is not sent twice.
    private readonly ConcurrentDictionary<string, bool> _sending = new(StringComparer.Ordinal);
    // Counts the session and profile changes: an answer for an earlier profile changes nothing of the next one's.
    private int _profiles;

    public MediaCardActions(CatalogApi catalog, ISessionManager session, INavigationService navigation, ILogger<MediaCardActions> logger,
        ViewerStateChanges? changes = null, RequestsApi? requests = null, RequestsFeature? requestsFeature = null)
    {
        _catalog = catalog;
        _session = session;
        _navigation = navigation;
        _logger = logger;
        _changes = changes;
        _requests = requests;
        _requestsFeature = requestsFeature;
        // Another profile has its own state; the next pages show it as the server knows it.
        _session.Changed += (_, _) =>
        {
            Interlocked.Increment(ref _profiles);
            _changed.Clear();
        };
    }

    /// <summary>Raised with the content id whose state changed (and when a change went back).</summary>
    public event EventHandler<string>? Changed;

    public MediaCardState StateOf(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _changed.TryGetValue(card.Id, out var state)
            ? state
            : new MediaCardState(card.IsWatched, card.IsFavorite, card.InWatchlist, card.IsNotInterested);
    }

    public static bool CanPlay(MediaCard card) => card.PlayContentId is { Length: > 0 };

    public static bool CanMarkWatched(MediaCard card) => DetailFacts.IsVideo(card.Type);

    /// <summary>A movie or episode with a resume point or watched: the web offers "Play from Beginning".</summary>
    public static bool CanPlayFromBeginning(MediaCard card, MediaCardState state) =>
        IsPlayable(card) && (HasResumePoint(card, state) || state.IsWatched);

    /// <summary>A movie or episode the viewer stopped in: "Reset Progress" clears the resume point.</summary>
    public static bool CanResetProgress(MediaCard card, MediaCardState state) => IsPlayable(card) && HasResumePoint(card, state);

    /// <summary>Not interested takes movies and series; the server refuses seasons and episodes.</summary>
    public static bool CanMarkNotInterested(MediaCard card) => card.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;

    private static bool IsPlayable(MediaCard card) => card.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode;

    private static bool HasResumePoint(MediaCard card, MediaCardState state) => card.Progress is > 0 and < 1 && !state.IsProgressCleared;

    /// <summary>Favourites and the watchlist hold movies and series, as on the detail page.</summary>
    public static bool CanCollect(MediaCard card) => card.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;

    /// <summary>A title from TMDB, while the server takes requests and keeps TMDB titles on the watchlist, as the web asks.</summary>
    public bool CanToggleTitleWatchlist(MediaCard card) =>
        card.Type == RequestsViewModel.CardType && RequestCards.TitleOf(card.Id) is not null && _requests is not null
        && _requestsFeature?.Status is { RequestsEnabled: true, WatchlistTitlesSupported: true };

    /// <summary>A TMDB title's watchlist change is on its way; the toggle waits for it.</summary>
    public bool IsSending(MediaCard card) => _sending.ContainsKey(card.Id);

    /// <summary>
    /// The card's request badge: its own, or the state its watchlist add requested in this session, until a newer answer
    /// gives the card another badge.
    /// </summary>
    public string? StatusBadgeOf(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var state = StateOf(card);
        return state.StatusBadge is { } badge && card.StatusBadge == state.StatusBadgeOver ? badge : card.StatusBadge;
    }

    /// <summary>Starts what the card's Play stands for (a series' next episode), where the viewer left off.</summary>
    public void Play(MediaCard card)
    {
        if (card.PlayContentId is { Length: > 0 } contentId)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(contentId));
        }
    }

    /// <summary>Starts the movie or episode at its beginning, not where the viewer left off.</summary>
    public void PlayFromBeginning(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (IsPlayable(card))
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.FromStart(card.Id));
        }
    }

    public Task<bool> ResetProgressAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsProgressCleared = true },
            (server, _) => _catalog.ClearResumePositionAsync(server, card.Id));

    public Task<bool> ToggleNotInterestedAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsNotInterested = !state.IsNotInterested },
            (server, state) => _catalog.SetNotInterestedAsync(server, card.Id, state.IsNotInterested));

    public Task<bool> ToggleWatchedAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsWatched = !state.IsWatched },
            (server, state) => _catalog.SetWatchedAsync(server, card.Id, state.IsWatched));

    public Task<bool> ToggleFavoriteAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { IsFavorite = !state.IsFavorite },
            (server, state) => _catalog.SetFavoriteAsync(server, card.Id, state.IsFavorite));

    public Task<bool> ToggleWatchlistAsync(MediaCard card) =>
        ChangeAsync(card, state => state with { InWatchlist = !state.InWatchlist },
            (server, state) => _catalog.SetWatchlistAsync(server, card.Id, state.InWatchlist));

    /// <summary>
    /// Puts a title from TMDB on the watchlist or takes it off. An add can also request the title (the server's
    /// <c>watchlist_requests</c>); the card then shows the request's state.
    /// </summary>
    public async Task<bool> ToggleTitleWatchlistAsync(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!CanToggleTitleWatchlist(card) || RequestCards.TitleOf(card.Id) is not { } title || _session.ActiveServer is not { } server
            || !_sending.TryAdd(card.Id, true))
        {
            return false;
        }
        int profiles = Volatile.Read(ref _profiles);
        var before = StateOf(card);
        Show(card.Id, before with { InWatchlist = !before.InWatchlist });
        try
        {
            if (before.InWatchlist)
            {
                await _requests!.RemoveFromWatchlistAsync(server.BaseUri, title.MediaType, title.TmdbId);
            }
            else
            {
                var entry = await _requests!.AddToWatchlistAsync(server.BaseUri, title.MediaType, title.TmdbId);
                if (entry.Request.Status is { Length: > 0 } && profiles == Volatile.Read(ref _profiles))
                {
                    Show(card.Id, StateOf(card) with { StatusBadge = RequestCards.Badge(entry.Request, null), StatusBadgeOver = card.StatusBadge });
                }
            }
            if (profiles == Volatile.Read(ref _profiles))
            {
                // A title the library has is on the library's watchlist now, or off it.
                _changes?.Report();
            }
            return true;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the card goes back to what the server last said; the button's click must not fail.
            if (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("Changing the watchlist of {CardId} from its card failed: {Failure}", card.Id, ApiFailureText.Describe(ex));
            }
            else
            {
                _logger.LogError(ex, "Changing the watchlist of {CardId} from its card failed", card.Id);
            }
            if (profiles == Volatile.Read(ref _profiles))
            {
                Show(card.Id, before);
            }
            return false;
        }
        finally
        {
            _sending.TryRemove(card.Id, out _);
            Changed?.Invoke(this, card.Id);
        }
    }

    private async Task<bool> ChangeAsync(MediaCard card, Func<MediaCardState, MediaCardState> change, Func<Uri, MediaCardState, Task> send)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_session.ActiveServer is not { } server)
        {
            return false;
        }
        int profiles = Volatile.Read(ref _profiles);
        var before = StateOf(card);
        var after = change(before);
        Show(card.Id, after);
        try
        {
            await send(server.BaseUri, after);
            if (profiles == Volatile.Read(ref _profiles))
            {
                _changes?.Report();
            }
            return true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing {ContentId} from its card failed: {Failure}", card.Id, ApiFailureText.Describe(ex));
            // Another profile's cards show what the server says of them, not this one's change going back.
            if (profiles == Volatile.Read(ref _profiles))
            {
                Show(card.Id, before);
            }
            return false;
        }
    }

    private void Show(string contentId, MediaCardState state)
    {
        _changed[contentId] = state;
        Changed?.Invoke(this, contentId);
    }
}
