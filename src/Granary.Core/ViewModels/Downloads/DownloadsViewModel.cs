using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The Downloads page: the active profile's downloads on this PC as cards in a library's grid (<see cref="DownloadCards"/>),
/// the films by title, then the series by title, each series one card that opens its episodes
/// (<see cref="DownloadedSeriesViewModel"/>), as the Apple app's series open their own screen. The web has no such page, so
/// its words are the apps'. At the top the space the downloads use and the space left. It follows the downloads between
/// <see cref="Attach"/> and <see cref="Detach"/> (<see cref="DownloadsFollower"/>): a transfer that only went further updates
/// its card and the space used without reading the other downloads. Cards change in place (<see cref="LiveCard"/>), so a card
/// keeps its place and the focus while its download changes state; one comes or goes only when its download does. The free
/// space is read off the UI thread, when the page shows and when a download comes, goes or finishes; a read that fails or
/// takes longer than <see cref="DiskSpaceReads.Timeout"/> leaves it unknown, and the top then says only the space used. Above
/// the cards it offers to delete the watched downloads to free up space, as both apps do, in the Android app's words
/// throughout (its Reclaim watched button, the dialog of that name, its question and its Reclaim), unless Settings →
/// Downloads keeps watched downloads; nothing is deleted without the question. After a start that could not reach the server
/// it says so above the cards, with a Retry that opens Home once the server answers (<see cref="OfflineStart"/>); while episodes
/// wait for your Silo server's limit of downloads, how many, with Cancel (<see cref="ShowsWaitingNote"/>). Beside Reclaim
/// Watched, Download settings opens Settings → Downloads. Above the films, the Apple app's Monitored: each monitored series as a
/// card (<see cref="MonitorCards"/>), running ones first, then by title, with what it takes and does next from the airing
/// calendar (<see cref="MonitorSchedule"/>, read as the page shows), which opens its download dialog on Monitor.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    /// <summary>The Android app's words for a page without downloads (<c>downloads_empty_title</c>), and how to get some.</summary>
    public static string EmptyTitle => AppText.Get("granary.downloads.empty.title");
    public static string EmptyText => AppText.Get("granary.downloads.empty.text");

    /// <summary>The films' and the series' headings, the Apple app's names for them (<c>downloads.storage.movies</c>, <c>.series</c>).</summary>
    public static string FilmsTitle => AppText.Get("granary.downloads.movies");
    public static string SeriesTitle => AppText.Get("granary.downloads.series");

    /// <summary>The Apple app's title for the monitored series (<c>downloads.monitored.title</c>).</summary>
    public static string MonitoredTitle => AppText.Get("granary.downloads.monitored");

    /// <summary>The button on the page and the title of its question (Android's <c>downloads_reclaim_watched</c>).</summary>
    public static string ReclaimTitle => AppText.Get("granary.downloads.reclaim.title");

    /// <summary>The question's yes (Android's <c>downloads_reclaim_confirm</c>).</summary>
    public static string ReclaimAction => AppText.Get("granary.downloads.reclaim.confirm");

    /// <summary>What the page says after a start without the server, beside its Retry.</summary>
    public static string OfflineNote => AppText.Get("granary.downloads.offlineNote");

    /// <summary>The page's way to Settings → Downloads (<see cref="OpenSettingsCommand"/>).</summary>
    public static string SettingsTitle => AppText.Get("granary.downloads.settings");

    private static readonly StringComparer TitleOrder = StringComparer.CurrentCultureIgnoreCase;

    private readonly DownloadManager _downloads;
    private readonly IDiskSpace _disk;
    private readonly IDownloadPreferences _preferences;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadsViewModel> _logger;
    private readonly OfflineStart? _offline;
    private readonly SeriesMonitors? _monitors;
    private readonly MonitorSchedule? _schedule;
    private readonly DownloadsFollower _follow;

    // How often the page was shown: a Retry's answer moves only the showing it was pressed in.
    private readonly Lock _gate = new();
    private int _attaches;

    // On the UI thread: the downloads as last read, the cards, and the space as last known.
    private readonly Dictionary<string, DownloadItem> _items = new(StringComparer.Ordinal);
    private readonly ObservableCollection<LiveCard> _films = [];
    private readonly ObservableCollection<LiveCard> _series = [];
    private readonly ObservableCollection<LiveCard> _monitored = [];
    private HashSet<string> _shownIds = new(StringComparer.Ordinal);
    private int _completed;
    private IReadOnlyList<DownloadItem> _reclaimable = [];
    private long _used;
    private long? _free;
    private int _reads;

    // The folder the free space was last read for: Settings → Downloads can name another while the page stays in memory.
    private string? _spaceFolder;

    public DownloadsViewModel(DownloadManager downloads, IDiskSpace disk, IDownloadPreferences preferences, INavigationService navigation,
        IConfirmation confirmation, IUiDispatcher dispatcher, TimeProvider time, ILogger<DownloadsViewModel> logger, OfflineStart? offline = null,
        SeriesMonitors? monitors = null, MonitorSchedule? schedule = null, DownloadActions? downloadActions = null, DownloadOfferReader? offers = null)
    {
        _downloads = downloads;
        _disk = disk;
        _preferences = preferences;
        _navigation = navigation;
        _confirmation = confirmation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _offline = offline;
        _monitors = monitors;
        _schedule = schedule;
        _follow = new DownloadsFollower(downloads, dispatcher, time, Refresh);
        Actions = new DownloadCardActions(downloads, navigation, confirmation);
        if (downloadActions is not null && offers is not null)
        {
            // A monitored series' dialog reads the series as its card's Download does, and opens on Monitor.
            Actions.OpenMonitor = card => downloadActions.OfferReadAsync(
                cancellationToken => offers.ReadAsync(card.Id, Api.Catalog.CatalogItemTypes.Series, cancellationToken),
                new SeriesMonitorTarget(card.Id, card.Title), startsOnMonitor: true);
        }
        Films = new ReadOnlyObservableCollection<LiveCard>(_films);
        Series = new ReadOnlyObservableCollection<LiveCard>(_series);
        Monitored = new ReadOnlyObservableCollection<LiveCard>(_monitored);
    }

    /// <summary>"12.0 GB used · 200.0 GB free"; only the space used while the free space is not known.</summary>
    [ObservableProperty]
    public partial string SpaceText { get; private set; } = "";

    /// <summary>The films' downloads, by title.</summary>
    public ReadOnlyObservableCollection<LiveCard> Films { get; }

    /// <summary>A card for each series with downloads, by title.</summary>
    public ReadOnlyObservableCollection<LiveCard> Series { get; }

    /// <summary>A card for each monitored series, running ones first, then by title.</summary>
    public ReadOnlyObservableCollection<LiveCard> Monitored { get; }

    [ObservableProperty]
    public partial bool HasMonitored { get; private set; }

    /// <summary>The last read of the airing calendar and the monitored series' titles; done once the cards show it.</summary>
    public Task ScheduleRead { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial bool HasFilms { get; private set; }

    [ObservableProperty]
    public partial bool HasSeries { get; private set; }

    /// <summary>What the cards do: their click (<see cref="OpenCardCommand"/>) and their ⋮ menu's entries.</summary>
    public DownloadCardActions Actions { get; }

    /// <summary>The profile has no downloads on this PC: the page says how to get some (<see cref="EmptyTitle"/>, <see cref="EmptyText"/>).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// The profile has watched downloads on this PC and Settings → Downloads does not keep them: the page offers to delete
    /// them (<see cref="ReclaimTitle"/>, <see cref="ReclaimCommand"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReclaimCommand))]
    public partial bool ShowsReclaim { get; private set; }

    /// <summary>
    /// Granary started without its server and has not reached it since: the page says so (<see cref="OfflineNote"/>) and
    /// offers <see cref="RetryConnectionCommand"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsOfflineNote { get; private set; }

    /// <summary>
    /// Episodes of a season or a series wait for your Silo server's limit of downloads (<see cref="DownloadManager.WaitingCount"/>):
    /// the page says how many above the cards (<see cref="WaitingNote"/>), with Cancel (<see cref="CancelWaitingCommand"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsWaitingNote { get; private set; }

    /// <summary>"Your Silo server's download limit is reached. 6 episodes wait on this PC and start once the server allows more."</summary>
    [ObservableProperty]
    public partial string WaitingNote { get; private set; } = "";

    /// <summary>A Retry is asking the server.</summary>
    [ObservableProperty]
    public partial bool IsRetrying { get; private set; }

    /// <summary>The last read of the free space; done once <see cref="SpaceText"/> shows it.</summary>
    public Task SpaceRead { get; private set; } = Task.CompletedTask;

    /// <summary>The page shows, on the UI thread: it lists the downloads and follows them until <see cref="Detach"/>.</summary>
    public void Attach()
    {
        if (!_follow.Attach())
        {
            return;
        }
        lock (_gate)
        {
            _attaches++;
        }
        // Keep watched downloads, switched in Settings while the page stays in memory, shows at once.
        _preferences.Changed += OnPreferencesChanged;
        if (_offline is not null)
        {
            _offline.PropertyChanged += OnOfflineChanged;
        }
        if (_monitors is not null)
        {
            _monitors.Changed += OnMonitorsChanged;
        }
        if (_schedule is not null)
        {
            _schedule.Changed += OnScheduleChanged;
        }
        ShowOffline();
        Show(readSpace: true);
        ScheduleRead = ReadScheduleAsync();
        // A media file deleted by hand since the last look: its card says "Missing file" once the disk was read.
        _downloads.LookAtFiles();
    }

    /// <summary>
    /// Settings → Downloads, over the page as Settings always opens, so Back leads here again. Neither of Silo's apps has a way
    /// there from its Downloads screen; it is Granary's.
    /// </summary>
    [RelayCommand]
    private void OpenSettings() => _navigation.NavigateTo(AppRoute.Settings, "downloads");

    /// <summary>
    /// The waiting note's Cancel: the active profile's waiting episodes leave the line, and none of them is asked for. Nothing on
    /// this PC goes, so nothing is asked first.
    /// </summary>
    [RelayCommand]
    private async Task CancelWaitingAsync()
    {
        if (_downloads.ActiveScope is { } scope)
        {
            await _downloads.DropWaitingAsync(scope, null, CancellationToken.None);
        }
    }

    /// <summary>A card's click: a whole download plays, any other opens its page, a series' card opens its downloads.</summary>
    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            Actions.Invoke(card);
        }
    }

    /// <summary>
    /// Asks whether the watched downloads may go, in the Android app's words (<c>downloads_reclaim_message</c>: how many, and
    /// the space their files take), and deletes them on a yes: the ones the question counted that are still watched then.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ShowsReclaim))]
    private async Task ReclaimAsync()
    {
        var asked = _reclaimable;
        // The profile the question is about: the answer deletes its downloads, whichever is active by then.
        if (asked.Count == 0 || _downloads.ActiveScope is not { } scope)
        {
            return;
        }
        string message = AppText.Get("granary.downloads.reclaim.message", ("count", asked.Count), ("count_text", AppText.Number(asked.Count)),
            ("size", Size(asked.Sum(i => i.ExpectedBytes))));
        if (await _confirmation.AskAsync(ReclaimTitle, message, ReclaimAction))
        {
            await _downloads.DeleteWatchedAsync(scope, [.. asked.Select(i => i.DownloadId)], CancellationToken.None);
        }
    }

    /// <summary>
    /// Asks the server again after a start without it, and opens Home once it answers, as the sidebar's Home does: a
    /// download that plays in the minimized player plays on, which a new start (<see cref="INavigationService.ResetTo"/>)
    /// would close. It stays executable while it asks, so the button keeps the focus; a second press asks nothing more.
    /// An answer that comes after the page was left moves the viewer nowhere, also when they are back on it by then.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RetryConnectionAsync()
    {
        if (_offline is null || IsRetrying)
        {
            return;
        }
        IsRetrying = true;
        int shown;
        lock (_gate)
        {
            shown = _attaches;
        }
        bool reachable = false;
        try
        {
            reachable = await _offline.RetryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A command's failure would end the app: the note stays, and Retry can be pressed again.
            _logger.LogWarning("The server could not be asked again ({Failure})", ex.GetType().Name);
        }
        await _dispatcher.ResumeOnUi();
        IsRetrying = false;
        bool shows;
        lock (_gate)
        {
            shows = _follow.IsAttached && _attaches == shown;
        }
        if (reachable && shows)
        {
            _navigation.ShowSection(AppRoute.Home);
        }
    }

    /// <summary>The page was left: it stops following the downloads, which hold it no longer.</summary>
    public void Detach()
    {
        _follow.Detach();
        _preferences.Changed -= OnPreferencesChanged;
        if (_offline is not null)
        {
            _offline.PropertyChanged -= OnOfflineChanged;
        }
        if (_monitors is not null)
        {
            _monitors.Changed -= OnMonitorsChanged;
        }
        if (_schedule is not null)
        {
            _schedule.Changed -= OnScheduleChanged;
        }
    }

    public void Dispose() => Detach();

    // Any thread: a monitor came, went or changed. A series monitored since the page showed (a run at the start, a new monitor)
    // has its title, poster and airings read too; the calendar itself only when it is due.
    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        _follow.Poke();
        if (_follow.IsAttached)
        {
            ScheduleRead = ReadScheduleAsync();
        }
    }

    // Any thread: the calendar or a series' title was read.
    private void OnScheduleChanged(object? sender, EventArgs e) => _follow.Poke();

    // The calendar and the titles, as the Apple app's Monitored list reads them when it shows; never thrown at the page. What
    // was read shows through the schedule's Changed, on the UI thread.
    private async Task ReadScheduleAsync()
    {
        if (_schedule is null)
        {
            return;
        }
        try
        {
            await _schedule.RefreshAsync(force: false, CancellationToken.None).ConfigureAwait(false);
            await _dispatcher.ResumeOnUi();
            if (_follow.IsAttached)
            {
                ShowCards();
            }
        }
        catch (Exception ex)
        {
            // Nobody waits for it: logged by its kind, and the cards stay as they were.
            _logger.LogWarning("The monitored series' calendar could not be read ({Failure})", ex.GetType().Name);
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) => _follow.Poke();

    // Any thread: a Retry's answer, or the session leaving the server or profile the start was for.
    private void OnOfflineChanged(object? sender, PropertyChangedEventArgs e) => _dispatcher.Post(ShowOffline);

    private void ShowOffline() => ShowsOfflineNote = _offline?.IsOffline == true;

    private void Refresh(bool changed, IReadOnlyCollection<string> progressed)
    {
        if (changed)
        {
            Show(readSpace: false);
        }
        else
        {
            ShowProgress(progressed);
        }
    }

    // Everything again; the cards change in place.
    private void Show(bool readSpace)
    {
        var items = _downloads.Items;
        var ids = items.Select(i => i.DownloadId).ToHashSet(StringComparer.Ordinal);
        int completed = items.Count(i => i.State == DownloadState.Completed);
        // A download that came, went or finished changed what the drive has free; another downloads folder is another drive's.
        bool reread = readSpace || !ids.SetEquals(_shownIds) || completed != _completed
            || !string.Equals(_preferences.Folder, _spaceFolder, StringComparison.OrdinalIgnoreCase);
        _shownIds = ids;
        _completed = completed;
        _items.Clear();
        foreach (var item in items)
        {
            _items[item.DownloadId] = item;
        }
        ShowCards();
        _used = _downloads.BytesUsed();
        ShowReclaim(items);
        if (reread)
        {
            ReadSpace();
        }
        else
        {
            ShowSpace();
        }
    }

    // Transfers that went further: their bytes, their cards and the space used.
    private void ShowProgress(IReadOnlyCollection<string> downloadIds)
    {
        bool any = false;
        foreach (string id in downloadIds)
        {
            if (_items.TryGetValue(id, out var item))
            {
                _items[id] = item with { BytesDone = _downloads.BytesDone(id) };
                any = true;
            }
        }
        if (any)
        {
            ShowCards();
        }
        _used = _downloads.BytesUsed();
        ShowSpace();
    }

    // Films by title, then each series by title.
    private void ShowCards()
    {
        var films = _items.Values.Where(i => i.SeriesId is null)
            .OrderBy(i => i.Title, TitleOrder).ThenBy(i => i.DownloadId, StringComparer.Ordinal)
            .Select(i => DownloadCards.Download(i, _follow.RateOf(i.DownloadId))).ToList();
        var series = _items.Values.Where(i => i.SeriesId is not null).GroupBy(i => i.SeriesId!, StringComparer.Ordinal)
            .Select(g => DownloadCards.Series(g.Key, [.. g.OrderBy(i => i.DownloadId, StringComparer.Ordinal)]))
            .OrderBy(c => c.Title, TitleOrder).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        LiveCard.Sync(_films, films);
        LiveCard.Sync(_series, series);
        LiveCard.Sync(_monitored, MonitorCardsNow());
        _follow.Track(_items.Values);
        HasFilms = _films.Count > 0;
        HasSeries = _series.Count > 0;
        HasMonitored = _monitored.Count > 0;
        int waiting = _downloads.WaitingCount();
        ShowsWaitingNote = waiting > 0;
        WaitingNote = waiting > 0 ? DownloadOptionsViewModel.WaitingText(waiting) : "";
        // Episodes on their way are no empty page, also before the first of them is here.
        IsEmpty = !HasFilms && !HasSeries && !HasMonitored && waiting == 0;
    }

    // Each monitored series as the Apple app's Monitored list has it: running ones first, then by title.
    private List<MediaCard> MonitorCardsNow()
    {
        if (_monitors is null)
        {
            return [];
        }
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        long downloadsLimit = _preferences.StorageLimitGb * DownloadPreferenceLimits.BytesPerGb;
        return [.. _monitors.Monitors
            .Select(monitor =>
            {
                var info = _schedule?.Info(monitor.SeriesId);
                string title = info?.Title is { Length: > 0 } read ? read
                    : _monitors.TitleOf(monitor.SeriesId)
                    ?? _items.Values.Where(i => i.SeriesId == monitor.SeriesId).Select(DownloadCards.SeriesTitleOf).FirstOrDefault()
                    ?? SeriesTitle;
                var status = MonitorRules.StatusOf(monitor, MonitorCards.ActivityOf(_items.Values, monitor.SeriesId), _schedule?.Upcoming(monitor.SeriesId) ?? [],
                    MonitorCards.KnownEpisodes(_items.Values, monitor.SeriesId), _schedule?.IsKnown == true, now, downloadsLimit);
                return (monitor.Active, Card: MonitorCards.Card(monitor, title, info, status, now, zone));
            })
            .OrderBy(m => m.Active ? 0 : 1).ThenBy(m => m.Card.Title, TitleOrder).ThenBy(m => m.Card.Id, StringComparer.Ordinal)
            .Select(m => m.Card)];
    }

    // What the question counts: the watched downloads that are whole on this PC, unless the viewer keeps them. Their size is
    // the media files', as both apps count it; the subtitles and pictures kept beside them go too.
    private void ShowReclaim(IReadOnlyList<DownloadItem> items)
    {
        _reclaimable = _preferences.KeepWatched ? [] : [.. items.Where(i => i.IsReclaimable)];
        ShowsReclaim = _reclaimable.Count > 0;
    }

    // Windows can take its time over a drive that sleeps or a share: the free space is read on the thread pool and shown on the UI thread.
    private void ReadSpace()
    {
        int read = ++_reads;
        string folder = _preferences.Folder;
        if (!string.Equals(folder, _spaceFolder, StringComparison.OrdinalIgnoreCase))
        {
            // The space left in the folder before says nothing of this one: only the space used shows until it is read.
            _spaceFolder = folder;
            _free = null;
        }
        ShowSpace();
        SpaceRead = ReadSpaceAsync(folder, read);
    }

    // Unknown (null) when the drive fails or does not answer in time; the page goes on either way.
    private async Task ReadSpaceAsync(string folder, int read)
    {
        long? free = await _disk.FreeBytesAsync(folder, _time, _logger).ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        if (read == _reads)
        {
            _free = free;
            ShowSpace();
        }
    }

    private void ShowSpace()
    {
        SpaceText = _free is { } free
            ? AppText.Get("granary.downloads.space.usedFree", ("used", Size(_used)), ("free", Size(free)))
            : AppText.Get("granary.downloads.space.used", ("size", Size(_used)));
    }

    private static string Size(long bytes) => bytes > 0 ? PrePlayText.FileSize(bytes) : AppText.Get("web.common.format.size.bytes", ("size", 0));
}
