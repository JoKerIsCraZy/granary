using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>What the title page's big button does.</summary>
public enum TitlePrimaryAction
{
    /// <summary>Request movie / Request series (a series with seasons asks which first).</summary>
    Request,

    /// <summary>Open in library.</summary>
    OpenInLibrary,

    /// <summary>A state or a reason, greyed: "Requested", "Approved", "Blocked".</summary>
    None,
}

/// <summary>A season of a series from TMDB, as the title page's season row shows it.</summary>
public sealed record TitleSeason(string Name, string Meta, string Status, Uri? PosterUrl)
{
    public bool HasStatus => Status.Length > 0;

    public string AccessibleName => $"{Name}, {Meta}{(Status.Length > 0 ? ", " + Status : "")}";
}

/// <summary>
/// A movie or series known only from TMDB, as Silo's web title page shows it (<c>TitleDetail.tsx</c>,
/// <c>ExternalTitleContent.tsx</c>, <c>RequestActionBar.tsx</c>): laid out like a library title with request actions in
/// place of playback. Request movie / Request series (a series with seasons asks which ones), the request's state once
/// there is one, Cancel request while the viewer's own can still be withdrawn, Notify me when someone else's request
/// lands, the watchlist, IMDb and TMDB; then the seasons, the cast and more titles like it.
/// </summary>
public sealed partial class TitleViewModel : ObservableObject
{
    internal const string NotFoundTitle = "This item isn't available";
    internal const string NotFoundHint = "It may have been removed, or you may not have access to it.";

    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly RequestsFeature _feature;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<TitleViewModel> _logger;
    // Where each card of More like this opens.
    private readonly Dictionary<string, (AppRoute Route, object Parameter)> _destinations = new(StringComparer.Ordinal);
    private TitleRouteParameter? _title;
    private RequestMediaDetail? _detail;
    private string? _ownRequestId;
    private CancellationTokenSource _run = new();

    public TitleViewModel(ISessionManager session, RequestsApi requests, RequestsFeature feature, INavigationService navigation, TimeProvider time,
        ILogger<TitleViewModel> logger, CardRequestNotice? requestNotice = null)
    {
        _session = session;
        _requests = requests;
        _feature = feature;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        RequestNotice = requestNotice ?? CardRequestNotice.None;
    }

    /// <summary>A More like this card's Request that failed, in the web's toast words.</summary>
    public CardRequestNotice RequestNotice { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasError), nameof(IsNotFound))]
    public partial LibraryLoadState State { get; private set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>TMDB does not know it, or the profile may not see it: the page says neither, as the item page.</summary>
    public bool IsNotFound => State == LibraryLoadState.Empty;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"SERIES · HBO", "MOVIE · WARNER BROS.".</summary>
    [ObservableProperty]
    public partial string Overline { get; private set; } = "";

    [ObservableProperty]
    public partial string Tagline { get; private set; } = "";

    [ObservableProperty]
    public partial string Overview { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? BackdropUrl { get; private set; }

    [ObservableProperty]
    public partial Uri? PosterUrl { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<DetailChip> Chips { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DetailRating> Ratings { get; private set; } = [];

    /// <summary>"12.3K votes" beside the TMDB score.</summary>
    [ObservableProperty]
    public partial string VotesText { get; private set; } = "";

    /// <summary>"Directed by X" or "Created by A, B".</summary>
    [ObservableProperty]
    public partial string CrewLine { get; private set; } = "";

    [ObservableProperty]
    public partial string Genres { get; private set; } = "";

    [ObservableProperty]
    public partial TitlePrimaryAction PrimaryAction { get; private set; }

    /// <summary>"Request movie", "Request series", "Open in library", "Requested", "Approved", "Blocked", …</summary>
    [ObservableProperty]
    public partial string PrimaryLabel { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPrimaryEnabled { get; private set; }

    /// <summary>Request series opens "Request seasons" first.</summary>
    [ObservableProperty]
    public partial bool NeedsSeasonPicker { get; private set; }

    [ObservableProperty]
    public partial bool ShowsWatchlist { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistLabel))]
    public partial bool IsInWatchlist { get; private set; }

    public string WatchlistLabel => IsInWatchlist ? "On Watchlist" : "Add to Watchlist";

    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    [ObservableProperty]
    public partial bool CanFollow { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowLabel))]
    public partial bool IsFollowing { get; private set; }

    public string FollowLabel => IsFollowing ? "Stop notifying me" : "Notify me when available";

    /// <summary>"Adding it to your watchlist also requests it."</summary>
    [ObservableProperty]
    public partial bool AddAlsoRequests { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownload))]
    public partial string DownloadText { get; private set; } = "";

    public bool HasDownload => DownloadText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadBar))]
    public partial double? DownloadPercent { get; private set; }

    public bool HasDownloadBar => DownloadPercent is not null;

    /// <summary>What an action did ("Added to your watchlist and requested"), or why it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsNoticeError { get; private set; }

    public bool HasNotice => Notice.Length > 0;

    /// <summary>
    /// An action is on its way, or went through and the title is being read again: the buttons wait until they say where
    /// things stand, so a second click cannot request the title twice.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCommand), nameof(ToggleWatchlistCommand), nameof(ToggleFollowCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>An action went through but the title could not be read after it: the buttons stay off until Reload.</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    [ObservableProperty]
    public partial Uri? ImdbUrl { get; private set; }

    [ObservableProperty]
    public partial Uri? TmdbUrl { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<TitleSeason> Seasons { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DetailPerson> Cast { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Recommendations { get; private set; } = [];

    /// <summary>The big button takes a click: it has something to do and no action is on its way.</summary>
    public bool CanAct => IsPrimaryEnabled && !IsBusy && !IsStale;

    public bool HasSeasons => Seasons.Count > 0;

    public bool HasCast => Cast.Count > 0;

    public bool HasRecommendations => Recommendations.Count > 0;

    /// <summary>Shows a title, once the server's request features are known (they decide the watchlist button).</summary>
    public async Task ShowAsync(TitleRouteParameter? title)
    {
        if (title is null)
        {
            return;
        }
        _title = title;
        RequestNotice.Attach();
        await _feature.RefreshAsync();
        await LoadAsync(quiet: false);
    }

    /// <summary>The page goes: its cards' requests are no longer its to tell.</summary>
    public void Deactivate() => RequestNotice.Detach();

    /// <summary>"Request seasons" for this series, from the detail shown.</summary>
    public RequestSeasonsViewModel? SeasonPicker() =>
        _detail is { MediaType: "series" } detail ? new RequestSeasonsViewModel(detail, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)) : null;

    /// <summary>Reads the title again: after a failure, or when an action went through but the page could not say so.</summary>
    [RelayCommand]
    private Task RetryAsync() => LoadAsync(quiet: false);

    /// <summary>Requests the title as it is: a movie, or a series without seasons to pick.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RequestAsync() => SendRequestAsync(seasons: null);

    /// <summary>Requests a series with what "Request seasons" picked.</summary>
    public Task RequestSeasonsAsync(RequestSeasonsViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        return picker.CanSubmit ? SendRequestAsync(picker.PickedSeasons) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task CancelAsync() => ActAsync("Couldn't cancel the request. Try again.", async (server, detail) =>
    {
        if (_ownRequestId is { } id)
        {
            await _requests.CancelAsync(server, id);
        }
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ToggleFollowAsync() => ActAsync("Couldn't change the notification. Try again.", (server, detail) =>
        _requests.SetFollowAsync(server, detail.MediaType, detail.TmdbId, follow: !IsFollowing));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ToggleWatchlistAsync() => ActAsync("Failed to update your watchlist", async (server, detail) =>
    {
        if (IsInWatchlist)
        {
            await _requests.RemoveFromWatchlistAsync(server, detail.MediaType, detail.TmdbId);
            Say("Removed from your watchlist");
            return;
        }
        var entry = await _requests.AddToWatchlistAsync(server, detail.MediaType, detail.TmdbId);
        Say(WatchlistAdded(entry, detail));
    });

    [RelayCommand]
    private void OpenInLibrary()
    {
        if (_detail?.LibraryContentId is { Length: > 0 } contentId)
        {
            _navigation.NavigateTo(AppRoute.Item, contentId);
        }
    }

    /// <summary>A title of More like this: the library's when it has it, else its own title page.</summary>
    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null && _destinations.TryGetValue(card.Id, out var destination))
        {
            _navigation.NavigateTo(destination.Route, destination.Parameter);
        }
    }

    private bool IsIdle() => !IsBusy && !IsStale;

    private Task SendRequestAsync(IReadOnlyList<int>? seasons) => ActAsync("Couldn't send the request. Try again.", async (server, detail) =>
    {
        if (!detail.Request.Requestable)
        {
            return;
        }
        // Not retried: a second POST after a lost answer would request the title twice.
        await _requests.CreateAsync(server, new MediaRequestCreate
        {
            MediaType = detail.MediaType,
            TmdbId = detail.TmdbId,
            Title = detail.Title,
            Year = detail.Year is > 0 ? detail.Year : null,
            Overview = string.IsNullOrWhiteSpace(detail.Overview) ? null : detail.Overview,
            PosterPath = string.IsNullOrWhiteSpace(detail.PosterPath) ? null : detail.PosterPath,
            BackdropPath = string.IsNullOrWhiteSpace(detail.BackdropPath) ? null : detail.BackdropPath,
            Seasons = seasons,
        });
        Say($"Requested {detail.Title}");
    });

    // An action, then the title read again so every button says where it stands; a failure says so and reads it too. The
    // buttons wait until then: until the read, the page still shows the state from before the action. When the read
    // fails after an action that went through, they stay off until Reload.
    private async Task ActAsync(string failure, Func<Uri, RequestMediaDetail, Task> act)
    {
        if (IsBusy || IsStale || _detail is not { } detail || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsBusy = true;
        Notice = "";
        bool acted;
        try
        {
            await act(server.BaseUri, detail);
            acted = true;
        }
        catch (Exception ex)
        {
            Log(ex, "A request action failed");
            Say(failure, isError: true);
            acted = false;
        }
        bool read = await LoadAsync(quiet: true);
        if (!read && acted)
        {
            IsStale = true;
            string done = Notice.TrimEnd('.');
            Say(done.Length > 0 ? $"{done}. Reload to see where it stands now." : "Reload to see where it stands now.", IsNoticeError);
            return;
        }
        IsBusy = false;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));

    partial void OnIsStaleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAct));
        RequestCommand.NotifyCanExecuteChanged();
        ToggleWatchlistCommand.NotifyCanExecuteChanged();
        ToggleFollowCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void Say(string text, bool isError = false)
    {
        IsNoticeError = isError;
        Notice = text;
    }

    // Whether the title is shown as the server has it now (a read another one replaced counts: that one shows it).
    private async Task<bool> LoadAsync(bool quiet)
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        if (_title is not { } title || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            State = LibraryLoadState.Idle;
            return false;
        }
        if (!quiet || State != LibraryLoadState.Ready)
        {
            _detail = null;
            Notice = "";
            RequestNotice.Clear();
            Title = title.Title;
            State = LibraryLoadState.Loading;
        }
        try
        {
            var detail = await _requests.GetDetailAsync(server.BaseUri, title.MediaType, title.TmdbId, token);
            string? own = await OwnCancellableRequestAsync(server.BaseUri, detail, token);
            if (token.IsCancellationRequested)
            {
                return true;
            }
            _detail = detail;
            _ownRequestId = own;
            Show(detail);
            State = LibraryLoadState.Ready;
            if (!quiet)
            {
                IsStale = false;
                IsBusy = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                return true;
            }
            if (quiet && State == LibraryLoadState.Ready)
            {
                Log(ex, "Could not read a title again");
                return false;
            }
            Log(ex, "Could not load a title");
            State = ex is SiloApiException { Status: 404 } ? LibraryLoadState.Empty : LibraryLoadState.Error;
            return false;
        }
    }

    // The viewer's own request, while they can still withdraw it. The detail names the request when it is the account's
    // own (or the viewer is an admin), so that one is read and kept only if the account made it; without the id, the
    // account's requests are searched for the title.
    private async Task<string?> OwnCancellableRequestAsync(Uri server, RequestMediaDetail detail, CancellationToken token)
    {
        if (detail.Request.Status is not ("pending" or "approved"))
        {
            return null;
        }
        try
        {
            if (detail.Request.RequestId is { Length: > 0 } id)
            {
                var request = await _requests.GetAsync(server, id, token);
                return request.RequestedByUserId is { } owner && owner == _session.Account?.Id && RequestText.CanCancel(request) ? request.Id : null;
            }
            // The account's open requests, as the web reads them: two pages hold the hundred it shows.
            string? cursor = null;
            for (int page = 0; page < 2; page++)
            {
                var mine = await _requests.ListMineAsync(server, cursor: cursor, outcome: "active", cancellationToken: token);
                if (mine.Items.FirstOrDefault(r => r.MediaType == detail.MediaType && r.TmdbId == detail.TmdbId && RequestText.CanCancel(r)) is { } own)
                {
                    return own.Id;
                }
                cursor = mine.Page is { HasMore: true, NextCursor: { Length: > 0 } next } && next != cursor ? next : null;
                if (cursor is null)
                {
                    break;
                }
            }
            return null;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !token.IsCancellationRequested)
        {
            // Without it the page offers no Cancel request; the Requests page still does.
            _logger.LogWarning(ex, "Could not tell whether a title's request is the viewer's own");
            return null;
        }
    }

    private void Show(RequestMediaDetail detail)
    {
        bool isSeries = detail.MediaType == "series";
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        Title = detail.Title;
        Overline = string.Join(" · ", new[] { isSeries ? "Series" : "Movie", TitleText.Studio(detail) }.Where(p => !string.IsNullOrWhiteSpace(p)))
            .ToUpperInvariant();
        Tagline = detail.Tagline ?? "";
        Overview = detail.Overview ?? "";
        BackdropUrl = TmdbImages.Poster(detail.BackdropPath, "original");
        PosterUrl = TmdbImages.Poster(detail.PosterPath, "w500");
        Chips = ChipsOf(detail, isSeries);
        Ratings = detail.VoteAverage is double score and > 0 and <= 10
            ? [new DetailRating("TMDB", score.ToString("0.0", CultureInfo.InvariantCulture))]
            : [];
        VotesText = Ratings.Count > 0 && detail.VoteCount is int votes and > 0 ? TitleText.Votes(votes) : "";
        IReadOnlyList<string> crew = isSeries ? detail.Creators : detail.Director is { Length: > 0 } director ? [director] : [];
        CrewLine = crew.Count == 0 ? "" : $"{(isSeries ? "Created by" : "Directed by")} {string.Join(", ", crew)}";
        Genres = string.Join(", ", detail.Genres);
        ShowActions(detail);
        ImdbUrl = detail.ImdbId is { Length: > 0 } imdb && imdb.All(char.IsAsciiLetterOrDigit) ? new Uri($"https://www.imdb.com/title/{imdb}") : null;
        TmdbUrl = new Uri($"https://www.themoviedb.org/{(isSeries ? "tv" : "movie")}/{detail.TmdbId.ToString(CultureInfo.InvariantCulture)}");
        Seasons = [.. detail.Seasons.Select(s => new TitleSeason(TitleText.SeasonName(s), TitleText.SeasonMeta(s), TitleText.SeasonStatus(s, today) ?? "",
            TmdbImages.Poster(s.PosterPath)))];
        Cast = [.. detail.Cast.OrderBy(c => c.Order).Where(c => c.Name.Length > 0)
            .Select(c => new DetailPerson(c.Name, string.IsNullOrWhiteSpace(c.Character) ? null : c.Character, TmdbImages.Poster(c.ProfilePath, "w185"), null))];
        _destinations.Clear();
        Recommendations = [.. detail.Recommendations.Where(r => r.TmdbId > 0 && r.MediaType is "movie" or "series").Select(CardOf)
            .DistinctBy(c => c.Id, StringComparer.Ordinal)];
        OnPropertyChanged(nameof(HasSeasons));
        OnPropertyChanged(nameof(HasCast));
        OnPropertyChanged(nameof(HasRecommendations));
    }

    private void ShowActions(RequestMediaDetail detail)
    {
        var request = detail.Request;
        string? state = RequestText.DisplayState(request.Status, null, request.State);
        bool inLibrary = detail.Availability == "available" || state == RequestStates.Available || !string.IsNullOrEmpty(detail.LibraryContentId);
        if (request.Requestable)
        {
            PrimaryAction = TitlePrimaryAction.Request;
            PrimaryLabel = detail.MediaType == "series" ? "Request series" : "Request movie";
        }
        else if (inLibrary && !string.IsNullOrEmpty(detail.LibraryContentId))
        {
            PrimaryAction = TitlePrimaryAction.OpenInLibrary;
            PrimaryLabel = "Open in library";
        }
        else
        {
            PrimaryAction = TitlePrimaryAction.None;
            // The viewer's side of a pending request: they asked, nobody has answered.
            PrimaryLabel = inLibrary ? "In the library"
                : state is not null ? state == RequestStates.Pending ? "Requested" : RequestText.StateLabel(state)
                : RequestText.ReasonLabel(request.Reason);
        }
        IsPrimaryEnabled = PrimaryAction != TitlePrimaryAction.None;
        OnPropertyChanged(nameof(CanAct));
        NeedsSeasonPicker = detail.MediaType == "series" && detail.Seasons.Count > 0;
        var status = _feature.Status;
        ShowsWatchlist = status is { RequestsEnabled: true, WatchlistTitlesSupported: true };
        IsInWatchlist = detail.InWatchlist;
        // The server requests a title added to the watchlist only while it has no request and the viewer may make one.
        AddAlsoRequests = ShowsWatchlist && status is { WatchlistRequests: true } && !detail.InWatchlist && !inLibrary && request.Requestable;
        CanCancel = _ownRequestId is not null;
        // Someone else's open request: the viewer can ask to hear when it lands instead of requesting the title again.
        CanFollow = state is not null && state != RequestStates.Available && request.Reason == "already_requested" && !request.RequestedByViewer;
        IsFollowing = request.Following;
        var now = _time.GetUtcNow();
        DownloadText = request.Download is { } download ? RequestText.Download(download, now) : "";
        DownloadPercent = request.Download is { } bar ? RequestText.DownloadPercent(bar) : null;
    }

    private static IReadOnlyList<DetailChip> ChipsOf(RequestMediaDetail detail, bool isSeries)
    {
        var chips = new List<DetailChip>();
        Add(TitleText.Years(detail));
        Add(detail.ContentRating);
        if (isSeries)
        {
            Add(detail.NumberOfSeasons is int seasons and > 0 ? DetailText.Count(seasons, "season", "seasons") : null);
            Add(detail.NumberOfEpisodes is int episodes and > 0 ? DetailText.Count(episodes, "episode", "episodes") : null);
            Add(detail.Status);
        }
        else
        {
            Add(DetailText.Runtime(detail.Runtime));
        }
        return chips;

        void Add(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                chips.Add(new DetailChip(text.Trim().ToUpperInvariant()));
            }
        }
    }

    private MediaCard CardOf(RequestMediaResult result)
    {
        var card = RequestCards.Of(result);
        if (RequestCards.Destination(result) is { } destination)
        {
            _destinations.TryAdd(card.Id, destination);
        }
        return card;
    }

    // As the web's toast after an add: whether it also requested the title, or why not.
    private string WatchlistAdded(WatchlistTitleEntry entry, RequestMediaDetail detail)
    {
        var before = detail.Request;
        var after = entry.Request;
        string available = $"We'll let you know when {detail.Title} is available.";
        if (!string.IsNullOrEmpty(entry.ItemId))
        {
            return "Added to your watchlist";
        }
        if (after.RequestedByViewer && !before.RequestedByViewer)
        {
            return $"Added to your watchlist and requested. {available}";
        }
        if (after.Following && !before.Following)
        {
            return $"Added to your watchlist. {available}";
        }
        if (!string.IsNullOrEmpty(after.Status))
        {
            return "Added to your watchlist";
        }
        if (!string.IsNullOrEmpty(after.Reason))
        {
            return $"Added to your watchlist. It wasn't requested: {RequestText.ReasonLabel(after.Reason).ToLowerInvariant()}.";
        }
        return "Added to your watchlist";
    }

    private void Log(Exception ex, string message)
    {
        if (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "{Message}", message);
        }
        else
        {
            _logger.LogError(ex, "{Message}", message);
        }
    }
}
