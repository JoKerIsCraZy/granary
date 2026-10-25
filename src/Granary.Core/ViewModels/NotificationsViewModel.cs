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
/// The Notifications inbox, as Silo's web shows it (<c>pages/Notifications.tsx</c>): All or Unread (with its count),
/// the entries newest first in pages of 25 with Load more, Mark all read (only what the first page saw, through its
/// <c>read_cutoff</c>), and the Preferences. Opening an unread entry marks it read; so does its own check button. The
/// sidebar's count (<see cref="NotificationBadge"/>) follows.
/// </summary>
public sealed partial class NotificationsViewModel : ObservableObject
{
    internal const string MarkAllFailedText = "Could not mark the displayed notifications read. Reload the inbox before trying again.";

    private static readonly SettingChoice All = new("all", "All");

    private readonly ISessionManager _session;
    private readonly NotificationsApi _notifications;
    private readonly NotificationBadge _badge;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationsViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private string? _nextCursor;
    private string _readCutoff = "";
    private bool _following;
    private CancellationTokenSource _run = new();

    public NotificationsViewModel(ISessionManager session, NotificationsApi notifications, NotificationBadge badge, INavigationService navigation,
        TimeProvider time, ILogger<NotificationsViewModel> logger)
    {
        _session = session;
        _notifications = notifications;
        _badge = badge;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        Preferences = new NotificationPreferencesViewModel(session, notifications, logger);
        Filters = FiltersFor(0);
    }

    public ObservableCollection<NotificationRowViewModel> Rows { get; } = [];

    public NotificationPreferencesViewModel Preferences { get; }

    /// <summary>All, and "Unread (3)".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Filters { get; private set; }

    [ObservableProperty]
    public partial SettingChoice SelectedFilter { get; set; } = All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>A failed Mark all read: the inbox needs reading again before another try.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarkAllFailed))]
    [NotifyCanExecuteChangedFor(nameof(MarkAllReadCommand))]
    public partial string MarkAllError { get; private set; } = "";

    /// <summary>A read mark or a later page that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    public bool IsUnreadOnly => SelectedFilter.Value == "unread";

    /// <summary>"No unread notifications" or "No notifications yet".</summary>
    public string EmptyTitle => IsUnreadOnly ? "No unread notifications" : "No notifications yet";

    public bool HasUnread => _badge.HasUnread;

    public bool HasMarkAllFailed => MarkAllError.Length > 0;

    public bool HasNotice => Notice.Length > 0;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>
    /// Shows the inbox: Back keeps it for the same profile; otherwise it is read again. The unread count is followed while
    /// the page shows, until <see cref="Deactivate"/>.
    /// </summary>
    public Task ActivateAsync(bool isReturn)
    {
        if (!_following)
        {
            _following = true;
            _badge.PropertyChanged += OnBadgeChanged;
            OnUnreadChanged();
        }
        bool shown = _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        return isReturn && shown ? Task.CompletedTask : LoadAsync();
    }

    /// <summary>Stops following the unread count when the page goes, so the long-lived count does not hold this page.</summary>
    public void Deactivate()
    {
        if (_following)
        {
            _following = false;
            _badge.PropertyChanged -= OnBadgeChanged;
        }
    }

    /// <summary>Reads the inbox from its newest entry, and the sidebar's count with it.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Rows.Clear();
        CanLoadMore = false;
        // A Load more cut off by this reload no longer says it is loading.
        IsLoadingMore = false;
        _nextCursor = null;
        _readCutoff = "";
        MarkAllError = "";
        Notice = "";
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        _loadedFor = Key();
        State = LibraryLoadState.Loading;
        try
        {
            var page = await _notifications.ListAsync(server.BaseUri, IsUnreadOnly, cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            _readCutoff = page.ReadCutoff;
            Show(page, server.BaseUri);
            State = Rows.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Another filter or visit took over.
                return;
            }
            Log(ex, "Could not load the notifications");
            State = LibraryLoadState.Error;
            return;
        }
        // The count comes after the list: it never holds the list back or fails it.
        try
        {
            await _badge.RefreshAsync(force: true, token);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not read the unread notification count");
        }
    }

    [RelayCommand(CanExecute = nameof(CanPressLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_nextCursor is not { } cursor || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        IsLoadingMore = true;
        Notice = "";
        try
        {
            var page = await _notifications.ListAsync(server.BaseUri, IsUnreadOnly, cursor, token);
            if (!token.IsCancellationRequested)
            {
                Show(page, server.BaseUri);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log(ex, "Could not load more notifications");
                Notice = "Couldn't load more notifications. Try again.";
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

    /// <summary>Marks what the first page saw read; newer entries stay unread.</summary>
    [RelayCommand(CanExecute = nameof(CanMarkAllRead))]
    private async Task MarkAllReadAsync()
    {
        if (_readCutoff.Length == 0 || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        try
        {
            // Not cancelled by a reload: once sent, the inbox is read again after it, whatever the filter is by then.
            await _notifications.MarkAllReadAsync(server.BaseUri, _readCutoff);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log(ex, "Could not mark the notifications read");
                MarkAllError = MarkAllFailedText;
            }
            return;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private void Open(NotificationRowViewModel? row)
    {
        if (row?.Opens is not { } id)
        {
            return;
        }
        if (row.IsUnread)
        {
            MarkRead(row);
        }
        _navigation.NavigateTo(AppRoute.Item, id);
    }

    [RelayCommand]
    private void MarkRead(NotificationRowViewModel? row)
    {
        if (row is { IsUnread: true })
        {
            _ = MarkReadAsync(row);
        }
    }

    // The count in "Unread (3)" changing is the same filter: only another one reads the inbox again.
    partial void OnSelectedFilterChanged(SettingChoice oldValue, SettingChoice newValue)
    {
        if (oldValue.Value == newValue.Value)
        {
            return;
        }
        OnPropertyChanged(nameof(IsUnreadOnly));
        OnPropertyChanged(nameof(EmptyTitle));
        if (_loadedFor is not null)
        {
            _ = LoadAsync();
        }
    }

    // Shown read at once, as the web; one that fails to save comes back unread with a notice.
    private async Task MarkReadAsync(NotificationRowViewModel row)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        row.IsUnread = false;
        _badge.Show(_badge.UnreadCount - 1);
        try
        {
            await _notifications.MarkReadAsync(server.BaseUri, row.Id);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not mark a notification read");
            row.IsUnread = true;
            if (!token.IsCancellationRequested)
            {
                // After a reload the rows are as the server has them; only the count needs reading again.
                Notice = "Failed to mark notification read";
            }
            try
            {
                await _badge.RefreshAsync(force: true);
            }
            catch (Exception refreshFailure)
            {
                Log(refreshFailure, "Could not read the unread notification count");
            }
        }
    }

    private void Show(NotificationPage page, Uri server)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var shown = Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in page.Items.Where(i => !string.IsNullOrWhiteSpace(i.Id) && shown.Add(i.Id)))
        {
            Rows.Add(new NotificationRowViewModel(item.Id, NotificationText.Title(item), NotificationText.Description(item),
                NotificationText.When(TimeZoneInfo.ConvertTime(item.CreatedAt, zone), now), NotificationText.ReasonLabels(item),
                ArtworkUrl.Resolve(server, item.PosterUrl),
                // Episodes and fulfilled requests open their title; approved and declined requests have none yet.
                NonEmpty(item.EpisodeId) ?? NonEmpty(item.SeriesId), item.IsUnread, OpenCommand, MarkReadCommand));
        }
        // A page that says there is more without saying where would ask for itself again.
        _nextCursor = page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } && next != _nextCursor ? next : null;
        CanLoadMore = _nextCursor is not null;
    }

    private void OnBadgeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationBadge.UnreadCount))
        {
            OnUnreadChanged();
        }
    }

    private void OnUnreadChanged()
    {
        Filters = FiltersFor(_badge.UnreadCount);
        // The list of filters is new: the picked one is its equal in it.
        SelectedFilter = Filters.First(f => f.Value == SelectedFilter.Value);
        OnPropertyChanged(nameof(HasUnread));
        MarkAllReadCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<SettingChoice> FiltersFor(int unread) =>
        [All, new("unread", unread > 0 ? $"Unread ({unread})" : "Unread")];

    private bool CanPressLoadMore() => CanLoadMore && !IsLoadingMore;

    private bool CanMarkAllRead() => _badge.HasUnread && MarkAllError.Length == 0;

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

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
