using System.Collections.Concurrent;
using System.Globalization;
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

/// <summary>How a card's Request answered, in the web's toast words.</summary>
/// <param name="State">The request's state in words ("Pending") once it went through; null when it failed or the server named none.</param>
public sealed record CardRequestAnswer(string CardId, string Title, bool Succeeded, string? State = null)
{
    public string Text => Succeeded ? "Request submitted" : "Failed to submit request";
}

/// <summary>
/// What a card's own buttons and its menu do, as Silo's web card menu: play, play from the beginning, mark watched,
/// reset progress, favourite, watchlist and Not interested. Changes show at once and go back when the server refuses them. Cards are records of what the server said, and a scrolled list
/// shows them again in recycled containers, so the changes of this session are kept here by content id until the
/// session or profile changes. A title from TMDB (a Requests card) has the web's watchlist toggle in its corner, while
/// the server keeps a watchlist of TMDB titles, and Request in its middle while the viewer may request it.
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
    // TMDB titles whose request is on its way, with the owner count it was sent under.
    private readonly ConcurrentDictionary<string, int> _requesting = new(StringComparer.Ordinal);
    // What this owner requested from a card, and the state the server gave it: it holds for that very card (its
    // RequestOffer), while a card read again says where the title stands now (and offers Request again when the request
    // was cancelled or declined). Apart from _changed: an account read again, same profile, keeps what was requested.
    private readonly ConcurrentDictionary<string, (MediaRequestCreate Offer, string? Badge)> _requested = new(StringComparer.Ordinal);
    // Counts the session and profile changes: an answer for an earlier profile changes nothing of the next one's.
    private int _profiles;
    // The server and profile requests are made for, and how often they changed: only a real change lets requests go,
    // since one the server may already have taken would be sent again.
    private readonly Lock _ownerLock = new();
    private (string? ServerId, string? ProfileId) _owner;
    private int _owners;
    private CancellationTokenSource _ownerRun = new();

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
        _owner = OwnerNow();
        // Another profile has its own state; the next pages show it as the server knows it. Its cards neither say Sending
        // nor wait for the last profile's requests.
        _session.Changed += (_, _) =>
        {
            Interlocked.Increment(ref _profiles);
            _changed.Clear();
            ForgetRequestsOfAnotherOwner();
        };
    }

    private (string? ServerId, string? ProfileId) OwnerNow() => (_session.ActiveServer?.Id, _session.ProfileId);

    // The state is swapped under the lock; the old owner's requests are cancelled after it, since cancelling runs their
    // callbacks, which must not run while the lock is held.
    private void ForgetRequestsOfAnotherOwner()
    {
        CancellationTokenSource replaced;
        lock (_ownerLock)
        {
            var owner = OwnerNow();
            if (owner == _owner)
            {
                return;
            }
            _owner = owner;
            Interlocked.Increment(ref _owners);
            _requesting.Clear();
            _requested.Clear();
            replaced = _ownerRun;
            _ownerRun = new CancellationTokenSource();
        }
        replaced.Cancel();
        replaced.Dispose();
    }

    // Whether the owner a request was sent for is still the one, and if so what it made, at once: another owner taking
    // over in between clears what was requested, and must not find this owner's answer written after it.
    private bool StillOwner(int owners, Action? whileOwner = null)
    {
        lock (_ownerLock)
        {
            if (owners != _owners)
            {
                return false;
            }
            whileOwner?.Invoke();
            return true;
        }
    }

    /// <summary>Raised with the content id whose state changed (and when a change went back).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>A card's Request answered, for the page to say so as the web's toast does; not for an earlier profile's.</summary>
    public event EventHandler<CardRequestAnswer>? RequestAnswered;

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
    /// A title from TMDB the viewer may request, while the server takes requests and this session has not requested it yet:
    /// the web's <c>RequestAction</c>, shown for <c>request.requestable</c>.
    /// </summary>
    public bool CanRequest(MediaCard card) =>
        card.Type == RequestsViewModel.CardType && card.RequestOffer is { } offer && _requests is not null
        && _requestsFeature?.Status is { RequestsEnabled: true } && !(_requested.TryGetValue(card.Id, out var made) && ReferenceEquals(made.Offer, offer));

    /// <summary>A TMDB title's request is on its way for this profile: the action says Sending and waits, also after the pointer left.</summary>
    public bool IsRequesting(MediaCard card) =>
        _requesting.TryGetValue(card.Id, out int owners) && owners == Volatile.Read(ref _owners);

    /// <summary>Some card's request is on its way: a card without one need not look itself up.</summary>
    public bool HasRequestsInFlight => !_requesting.IsEmpty;

    /// <summary>"Request", or "Sending" while the request is on its way.</summary>
    public string RequestActionText(MediaCard card) => IsRequesting(card) ? "Sending" : "Request";

    /// <summary>"Request The Matrix (Movie · 1999)", or "Sending request for …" while on its way, as the web's label.</summary>
    public string RequestActionName(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var offer = card.RequestOffer;
        string kind = RequestText.MediaTypeLabel(offer?.MediaType ?? "");
        string meta = offer?.Year is int year ? $"{kind} · {year.ToString(CultureInfo.InvariantCulture)}" : kind;
        string title = $"{card.Title} ({meta})";
        return IsRequesting(card) ? $"Sending request for {title}" : $"Request {title}";
    }

    /// <summary>
    /// The card's request badge: its own, the state its Request made in this session (for that very card; one read again
    /// says where the title stands now), or the state its watchlist add requested, until a newer answer gives the card
    /// another badge.
    /// </summary>
    public string? StatusBadgeOf(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.RequestOffer is { } offer && _requested.TryGetValue(card.Id, out var made) && ReferenceEquals(made.Offer, offer))
        {
            return made.Badge ?? card.StatusBadge;
        }
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

    /// <summary>
    /// Requests a title from TMDB from its card, as the web's hover Request: the title as TMDB gave it, a series whole (only
    /// the title page asks for seasons). The card then shows the request's state and offers Request no more, until the title
    /// is read again. Not retried: a second POST after a lost answer would request it twice. Another server or profile
    /// taking over lets the request go: its answer changes nothing. The account read again, same profile, does not.
    /// </summary>
    public async Task<bool> RequestAsync(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        int owners;
        CancellationToken cancellationToken;
        lock (_ownerLock)
        {
            owners = _owners;
            cancellationToken = _ownerRun.Token;
        }
        if (!CanRequest(card) || card.RequestOffer is not { } offer || _session.ActiveServer is not { } server || !_requesting.TryAdd(card.Id, owners))
        {
            return false;
        }
        Changed?.Invoke(this, card.Id);
        try
        {
            var request = await _requests!.CreateAsync(server.BaseUri, offer, cancellationToken);
            string? badge = RequestCards.Badge(request);
            if (StillOwner(owners, () => _requested[card.Id] = (offer, badge)))
            {
                RequestAnswered?.Invoke(this, new CardRequestAnswer(card.Id, card.Title, Succeeded: true, RequestCards.StateText(request)));
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another server or profile took over.
            return false;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the card offers Request again; the button's click must not fail.
            if (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("Requesting {CardId} from its card failed: {Failure}", card.Id, ApiFailureText.Describe(ex));
            }
            else
            {
                _logger.LogError(ex, "Requesting {CardId} from its card failed", card.Id);
            }
            if (StillOwner(owners))
            {
                RequestAnswered?.Invoke(this, new CardRequestAnswer(card.Id, card.Title, Succeeded: false));
            }
            return false;
        }
        finally
        {
            // Only this owner's: the next one may have sent its own for the title meanwhile.
            _requesting.TryRemove(new KeyValuePair<string, int>(card.Id, owners));
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
