using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Images;
using Granary.Core.Api.Notifications;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Series view of the notifications page, as the web's (<c>components/notifications/NotifiedSeriesList.tsx</c>): every
/// series whose new episodes notify the profile, then under "Won't notify" the ones its preferences silence or it
/// muted. Both lists load as the viewer scrolls, the second once the first is complete; a search, a reason and a sort
/// apply to both. Where the server can, each series mutes and unmutes, and a mute can be undone.
/// </summary>
public sealed partial class NotifiedSeriesViewModel : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    internal const string LoadMoreFailedText = "Couldn’t load more series.";

    private readonly ISessionManager _session;
    private readonly NotificationsApi _notifications;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private NotifiedSeriesQuery _applied;
    private NotificationPreferences? _preferences;
    private string? _activeCursor;
    private string? _inactiveCursor;
    private (string Id, string Title)? _undo;
    private CancellationTokenSource _run = new();
    private CancellationTokenSource? _searchRun;

    internal NotifiedSeriesViewModel(ISessionManager session, NotificationsApi notifications, INavigationService navigation, TimeProvider time, ILogger logger)
    {
        _session = session;
        _notifications = notifications;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        Reason = ReasonChoices[0];
        Sort = SortChoices[0];
        _applied = Query();
    }

    public static IReadOnlyList<SettingChoice> ReasonChoices { get; } =
    [
        new("all", "All reasons"),
        new(NotifiedSeriesReasonKeys.Favorite, "Favorites"),
        new(NotifiedSeriesReasonKeys.Watchlist, "Watchlist"),
        new(NotifiedSeriesReasonKeys.ContinueWatching, "Continue Watching"),
        new(NotifiedSeriesReasonKeys.NextUp, "Next Up"),
    ];

    public static IReadOnlyList<SettingChoice> SortChoices { get; } =
    [
        new("title", "Title (A–Z)"),
        new("-title", "Title (Z–A)"),
        new("-latest_episode_added", "Latest episode"),
        new("-added_at", "Recently added"),
        new("-year", "Release year"),
    ];

    /// <summary>The series that notify.</summary>
    public ObservableCollection<NotifiedSeriesRowViewModel> Notifying { get; } = [];

    /// <summary>The series that won't, shown once <see cref="Notifying"/> is complete.</summary>
    public ObservableCollection<NotifiedSeriesRowViewModel> Silenced { get; } = [];

    /// <summary>The search box; the lists follow it after a pause in the typing.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial SettingChoice Reason { get; set; }

    [ObservableProperty]
    public partial SettingChoice Sort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady), nameof(IsFilteredEmpty), nameof(IsBareEmpty))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>
    /// Other filters or a mute are being read: the lists shown stay, dimmed, until the new ones come. Their pages are
    /// the old query's, so none is asked for meanwhile.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand), nameof(LoadMoreOnScrollCommand))]
    public partial bool IsRefreshing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand), nameof(LoadMoreOnScrollCommand))]
    public partial bool CanLoadMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand), nameof(LoadMoreOnScrollCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>"Couldn't load more series." with Try again; scrolling asks no more until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadMoreError))]
    public partial string LoadMoreError { get; private set; } = "";

    /// <summary>The "Won't notify" heading and its list.</summary>
    [ObservableProperty]
    public partial bool ShowsSilenced { get; private set; }

    /// <summary>"Muted Severance" (with Undo), or a mute that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoticeFailed { get; private set; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    /// <summary>The server mutes single series (its capability): the rows carry Mute.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntroEnd))]
    public partial bool CanMute { get; internal set; }

    /// <summary>The end of "Change which reasons count in Preferences, or mute one series."</summary>
    public string IntroEnd => CanMute ? ", or mute one series." : ".";

    public bool HasNotice => Notice.Length > 0;

    public bool HasLoadMoreError => LoadMoreError.Length > 0;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>Nothing matches the search or reason: "No series match."</summary>
    public bool IsFilteredEmpty => IsEmpty && IsFiltered;

    /// <summary>Nothing notifies the profile at all: "No series notify you yet".</summary>
    public bool IsBareEmpty => IsEmpty && !IsFiltered;

    /// <summary>The read the search box set off after its pause (for tests).</summary>
    internal Task Searching { get; private set; } = Task.CompletedTask;

    private bool IsFiltered => _applied.Search.Length > 0 || _applied.Reason is not null;

    /// <summary>Shows the view: read for this profile once, and kept on later visits.</summary>
    public Task ActivateAsync() =>
        _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading ? Task.CompletedTask : LoadAsync();

    /// <summary>Reads the lists again in place, as after a preference changed which series notify.</summary>
    public Task RefreshAsync() => _loadedFor is not null && _loadedFor == Key() ? LoadAsync() : Task.CompletedTask;

    /// <summary>Another profile or server: what was read for the last one goes, and a read on its way is dropped.</summary>
    public void ResetIfAnotherProfile()
    {
        if (_loadedFor is null || _loadedFor == Key())
        {
            return;
        }
        _searchRun?.Cancel();
        _run.Cancel();
        _loadedFor = null;
        Clear();
        IsRefreshing = false;
        IsLoadingMore = false;
        LoadMoreError = "";
        DismissNotice();
        State = LibraryLoadState.Idle;
    }

    /// <summary>Reads both lists from their first series with the current search, reason and sort.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        _searchRun?.Cancel();
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        LoadMoreError = "";
        IsLoadingMore = false;
        // A read this one cut off no longer says it is refreshing.
        IsRefreshing = false;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            Clear();
            State = LibraryLoadState.Idle;
            return;
        }
        var key = Key();
        bool inPlace = _loadedFor == key && State is LibraryLoadState.Ready or LibraryLoadState.Empty;
        _loadedFor = key;
        _applied = Query();
        if (inPlace)
        {
            IsRefreshing = true;
        }
        else
        {
            Clear();
            State = LibraryLoadState.Loading;
        }
        try
        {
            var preferences = ReadPreferencesAsync(server.BaseUri, token);
            var active = _notifications.ListSeriesAsync(server.BaseUri, NotifiedSeriesStatus.Active, _applied, cancellationToken: token);
            var inactive = _notifications.ListSeriesAsync(server.BaseUri, NotifiedSeriesStatus.Inactive, _applied, cancellationToken: token);
            await Task.WhenAll(active, inactive);
            _preferences = await preferences;
            if (token.IsCancellationRequested)
            {
                return;
            }
            Clear();
            Show(await active, NotifiedSeriesStatus.Active, server.BaseUri);
            Show(await inactive, NotifiedSeriesStatus.Inactive, server.BaseUri);
            // A page can hold no series and still have more behind it.
            State = Notifying.Count == 0 && Silenced.Count == 0 && !CanLoadMore ? LibraryLoadState.Empty : LibraryLoadState.Ready;
            // Empty before and after: which empty it is follows the filters.
            OnPropertyChanged(nameof(IsFilteredEmpty));
            OnPropertyChanged(nameof(IsBareEmpty));
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Other filters or another visit took over.
                return;
            }
            Log(ex, "Could not load the series that notify");
            Clear();
            State = LibraryLoadState.Error;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsRefreshing = false;
            }
        }
    }

    /// <summary>The next page: the notifying series' until they are complete, then the others'. Try again after a failure.</summary>
    [RelayCommand(CanExecute = nameof(CanPressLoadMore))]
    private async Task LoadMoreAsync()
    {
        var status = _activeCursor is not null ? NotifiedSeriesStatus.Active : NotifiedSeriesStatus.Inactive;
        if ((_activeCursor ?? _inactiveCursor) is not { } cursor || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        IsLoadingMore = true;
        LoadMoreError = "";
        try
        {
            var page = await _notifications.ListSeriesAsync(server.BaseUri, status, _applied, cursor, token);
            if (!token.IsCancellationRequested)
            {
                Show(page, status, server.BaseUri);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log(ex, "Could not load more series that notify");
                LoadMoreError = LoadMoreFailedText;
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingMore = false;
            }
        }
    }

    /// <summary>The list's foot came into view; after a failure only Try again asks again.</summary>
    [RelayCommand(CanExecute = nameof(CanPressLoadMore))]
    private Task LoadMoreOnScrollAsync() => HasLoadMoreError ? Task.CompletedTask : LoadMoreAsync();

    [RelayCommand]
    private void Open(NotifiedSeriesRowViewModel? row)
    {
        if (row is not null)
        {
            _navigation.NavigateTo(AppRoute.Item, row.ContentId);
        }
    }

    /// <summary>Mutes the row's series, or unmutes it; another row's button does not wait for it.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleMuteAsync(NotifiedSeriesRowViewModel? row) =>
        row is { IsPending: false } ? SetMutedAsync(row.ContentId, row.Title, !row.Muted) : Task.CompletedTask;

    /// <summary>Takes the last mute back; one that fails can be tried again.</summary>
    [RelayCommand]
    private Task UndoAsync() => _undo is { } undo ? SetMutedAsync(undo.Id, undo.Title, muted: false, isUndo: true) : Task.CompletedTask;

    [RelayCommand]
    private void DismissNotice()
    {
        Notice = "";
        CanUndo = false;
        _undo = null;
    }

    partial void OnSearchChanged(string value)
    {
        _searchRun?.Cancel();
        _searchRun?.Dispose();
        _searchRun = null;
        if (_loadedFor is null || value.Trim() == _applied.Search)
        {
            return;
        }
        var run = new CancellationTokenSource();
        _searchRun = run;
        Searching = SearchLaterAsync(run.Token);
    }

    partial void OnReasonChanged(SettingChoice value) => Reapply();

    partial void OnSortChanged(SettingChoice value) => Reapply();

    // Muted or unmuted, the lists are read again: the server decides where the series goes. The row's button waits
    // until then, since its label is still the old one.
    private async Task SetMutedAsync(string id, string title, bool muted, bool isUndo = false)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var key = Key();
        var rows = Notifying.Concat(Silenced).Where(r => r.ContentId == id).ToList();
        rows.ForEach(r => r.IsPending = true);
        DismissNotice();
        try
        {
            await _notifications.SetSeriesMutedAsync(server.BaseUri, id, muted);
            if (muted && Key() == key)
            {
                NoticeFailed = false;
                Notice = $"Muted {title}";
                _undo = (id, title);
                CanUndo = true;
            }
        }
        catch (Exception ex)
        {
            Log(ex, muted ? "Could not mute a series" : "Could not unmute a series");
            if (Key() == key)
            {
                NoticeFailed = true;
                Notice = muted ? "Failed to mute the series" : "Failed to unmute the series";
                if (isUndo)
                {
                    _undo = (id, title);
                    CanUndo = true;
                }
            }
        }
        try
        {
            await RefreshAsync();
        }
        finally
        {
            rows.ForEach(r => r.IsPending = false);
        }
    }

    private async Task SearchLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDelay, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await LoadAsync();
    }

    private void Reapply()
    {
        if (_loadedFor is not null && Query() != _applied)
        {
            _ = LoadAsync();
        }
    }

    // The preferences only word why a series is silenced: without them the lists still show.
    private async Task<NotificationPreferences?> ReadPreferencesAsync(Uri server, CancellationToken token)
    {
        try
        {
            return await _notifications.GetPreferencesAsync(server, token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log(ex, "Could not read the notification preferences");
            return null;
        }
    }

    private void Show(NotifiedSeriesPage page, NotifiedSeriesStatus status, Uri server)
    {
        var list = status == NotifiedSeriesStatus.Active ? Notifying : Silenced;
        var shown = list.Select(r => r.ContentId).ToHashSet(StringComparer.Ordinal);
        foreach (var series in page.Items)
        {
            if (series.Item is { ContentId: { Length: > 0 } id } item && shown.Add(id))
            {
                list.Add(new NotifiedSeriesRowViewModel(id, item.Title,
                    series.Notifies ? NotifiedSeriesText.Trigger(series) : NotifiedSeriesText.Silenced(series, _preferences), series.Notifies,
                    series.Muted, NotifiedSeriesText.Pills(series), ArtworkUrl.Resolve(server, item.PosterUrl), CanMute, OpenCommand, ToggleMuteCommand));
            }
        }
        string? previous = status == NotifiedSeriesStatus.Active ? _activeCursor : _inactiveCursor;
        // A page that says there is more without saying where would ask for itself again.
        string? next = page.Page is { HasMore: true, NextCursor: { Length: > 0 } cursor } && cursor != previous ? cursor : null;
        if (status == NotifiedSeriesStatus.Active)
        {
            _activeCursor = next;
        }
        else
        {
            _inactiveCursor = next;
        }
        CanLoadMore = _activeCursor is not null || _inactiveCursor is not null;
        ShowsSilenced = _activeCursor is null && Silenced.Count > 0;
    }

    private void Clear()
    {
        Notifying.Clear();
        Silenced.Clear();
        _activeCursor = null;
        _inactiveCursor = null;
        CanLoadMore = false;
        ShowsSilenced = false;
    }

    private NotifiedSeriesQuery Query() => new(Search.Trim(), Reason.Value == "all" ? null : Reason.Value, Sort.Value);

    private bool CanPressLoadMore() => CanLoadMore && !IsLoadingMore && !IsRefreshing;

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

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
