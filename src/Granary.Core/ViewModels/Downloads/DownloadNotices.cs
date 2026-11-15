using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// Says when downloads finished or failed (<see cref="DownloadActivity.Announced"/>; docs/architecture.md, "Downloads in the
/// shell"): while Granary's window is in front, a short notice in it (<see cref="NoticeText"/> for <see cref="NoticeShowsFor"/>,
/// as the player's notice card; amber when something failed); while it is not, a Windows notification, with Play when
/// something finished and Open Downloads, as the Apple app's download notifications lead to its Downloads screen. When Windows
/// takes no notification from Granary, the notice shows in the window all the same. <see cref="Act"/> is what a notification's
/// click does. The shell has one for the app; made and used on the UI thread.
/// </summary>
public sealed partial class DownloadNotices : ObservableObject, IDisposable
{
    /// <summary>How long the notice shows in the window.</summary>
    public static readonly TimeSpan NoticeShowsFor = TimeSpan.FromSeconds(5);

    /// <summary>A notification's button that plays what finished: the Downloads page's own word.</summary>
    public const string PlayLabel = DownloadCardActions.PlayLabel;

    /// <summary>A notification's button, and what its click does, that opens the Downloads page.</summary>
    public const string OpenDownloadsLabel = "Open Downloads";

    /// <summary>What a notification's click asks for (<see cref="Act"/>): play a download, or open the Downloads page.</summary>
    public const string PlayAction = "play";
    public const string OpenAction = "downloads";

    private readonly DownloadActivity _activity;
    private readonly IAppNotifications _notifications;
    private readonly DownloadManager _downloads;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _noticeTimer;
    private int _notices;
    private bool _disposed;

    public DownloadNotices(DownloadActivity activity, IAppNotifications notifications, DownloadManager downloads, INavigationService navigation,
        IUiDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(notifications);
        _activity = activity;
        _notifications = notifications;
        _downloads = downloads;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _activity.Announced += OnAnnounced;
    }

    /// <summary>"Example Movie is ready to watch offline" for <see cref="NoticeShowsFor"/>; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeText { get; private set; } = "";

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>Something did not download: the notice is amber, as the player's warnings.</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; private set; }

    /// <summary>
    /// A notification's click, on the UI thread: Play (<see cref="PlayAction"/>) plays the download from this PC when the active
    /// profile still has it whole; anything else, or a download no longer here, opens the Downloads page. Nothing without a
    /// profile signed in: the notification may be older than a sign-out.
    /// </summary>
    public void Act(string? action, string? downloadId)
    {
        if (_disposed || _downloads.ActiveScope is null)
        {
            return;
        }
        if (action == PlayAction && downloadId is not null && _downloads.Find(downloadId) is { IsPlayable: true } download)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(download.MediaItemId));
            return;
        }
        _navigation.ShowSection(AppRoute.Downloads);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _activity.Announced -= OnAnnounced;
        _noticeTimer?.Dispose();
        _noticeTimer = null;
    }

    // On the UI thread.
    private void OnAnnounced(object? sender, DownloadAnnouncement announcement)
    {
        if (_disposed || (!_notifications.IsAppInFront && _notifications.TryShow(announcement)))
        {
            return;
        }
        NoticeText = announcement.Text;
        IsWarning = announcement.IsFailure;
        int notice = ++_notices;
        _noticeTimer?.Dispose();
        _noticeTimer = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            // A newer notice keeps its own time.
            if (notice == _notices)
            {
                NoticeText = "";
            }
        }), null, NoticeShowsFor, Timeout.InfiniteTimeSpan);
    }
}
