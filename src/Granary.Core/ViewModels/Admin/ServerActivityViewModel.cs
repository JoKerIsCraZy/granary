using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Admin;
using Granary.Core.Realtime;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The activity indicator an acting administrator sees at the top right of every page, as the web's
/// (<c>components/ServerActivity.tsx</c>): a button with the number of things going on, and a panel that lists them. On
/// the admin area's pages it shows always, elsewhere only while something is going on, or may be, or its panel is open.
/// Its texts are the web's.
/// </summary>
/// <remarks>
/// The server's news comes on other threads and as fast as a scan counts its files. It is shown at once when nothing was
/// shown for <see cref="ShowInterval"/>, and otherwise once that time is up, whatever came meanwhile; who may see the
/// indicator at all is never kept waiting.
/// </remarks>
public sealed partial class ServerActivityViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the stream may be without a connection before the button says so, as the web waits.</summary>
    public static readonly TimeSpan ConnectionProblemDelay = TimeSpan.FromSeconds(4);

    /// <summary>The least time between two looks at the server's activity: four a second.</summary>
    public static readonly TimeSpan ShowInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The most scans the panel lists; the rest are counted in a line under them.</summary>
    public const int MaxScanRows = 25;

    /// <summary>The most running preparation jobs the panel lists; the rest join the line of those that wait.</summary>
    public const int MaxPreparationRows = 3;

    private readonly ServerActivity _activity;
    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _problem;
    private readonly ITimer _pace;
    private readonly Lock _paceGate = new();
    // Within ShowInterval of a look, and whether news came since: both the pace lock's.
    private bool _isPacing;
    private bool _hasNews;
    // What the last look showed of who may see the indicator; read on the threads the news comes on.
    private volatile bool _showsAvailable;
    private bool _isOffline;

    public ServerActivityViewModel(ServerActivity activity, IUiDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _activity = activity;
        _dispatcher = dispatcher;
        _problem = time.CreateTimer(_ => _dispatcher.Post(ShowConnectionProblem), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pace = time.CreateTimer(_ => OnPaced(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.Changed += OnActivityChanged;
        Show(_activity.State);
    }

    /// <summary>The server takes this profile as the acting administrator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsAvailable { get; private set; }

    /// <summary>The page shown is one of the admin area: the button stays while nothing is going on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool ShowsWhenIdle { get; set; }

    /// <summary>An open panel keeps its button, also when the last activity ends meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsPanelOpen { get; set; }

    /// <summary>
    /// Also while the scans may be more than are known: with none of them known there is nothing to count, but
    /// something may well be going on.
    /// </summary>
    public bool IsVisible => IsAvailable && (ShowsWhenIdle || Total > 0 || IsCountLowerBound || IsPanelOpen);

    /// <summary>Streams, running tasks, active scans and downloads being prepared, together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(HasActivity), nameof(BadgeText), nameof(Name), nameof(IsEmpty), nameof(HasSections))]
    public partial int Total { get; private set; }

    public bool HasActivity => Total > 0;

    /// <summary>
    /// There may be more scans than are known (<see cref="ServerActivityState.ScansMayBeMore"/>), so the counts with
    /// them in are the least there is. The web takes this from the number of scans it knows at the moment (500 or
    /// more); Granary keeps it from the snapshot until the next one, so scans that end do not make a guess exact.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(BadgeText), nameof(Name), nameof(ScanCountText))]
    public partial bool IsCountLowerBound { get; private set; }

    /// <summary>The red count on the button: "3", "99+", or "12+" for at least twelve.</summary>
    public string BadgeText => ServerActivityText.Count(Total, IsCountLowerBound);

    /// <summary>What a screen reader calls the button.</summary>
    public string Name => Total == 0 ? "Server activity"
        : IsCountLowerBound ? $"Server activity: at least {Total} active"
        : $"Server activity: {Total} active";

    /// <summary>The stream has had no connection for <see cref="ConnectionProblemDelay"/>: a red dot on the button.</summary>
    [ObservableProperty]
    public partial bool ShowsConnectionProblem { get; private set; }

    /// <summary>"Connecting…" or "Disconnected" in the panel's header; empty while the stream is live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionText))]
    public partial string ConnectionText { get; private set; } = "";

    public bool HasConnectionText => ConnectionText.Length > 0;

    /// <summary>
    /// Nothing is going on: the panel says so in place of its sections. Not before the scans are known, which come with
    /// the stream alone, as the web waits for them, and not over a download that failed to be prepared, which would
    /// have nowhere else to be seen.
    /// </summary>
    public bool IsEmpty => Total == 0 && AreScansKnown && !HasFailedPreparations;

    /// <summary>The stream said which scans there are, all of them, or the server offers none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasSections))]
    public partial bool AreScansKnown { get; private set; } = true;

    public bool HasSections => !IsEmpty;

    /// <summary>The server offers its playback sessions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoStreams))]
    public partial bool ShowsStreams { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStreams), nameof(HasNoStreams), nameof(StreamCountText))]
    public partial int StreamCount { get; private set; }

    public bool HasStreams => StreamCount > 0;

    /// <summary>"No active streams" in place of the rows.</summary>
    public bool HasNoStreams => ShowsStreams && !HasStreams;

    public string StreamCountText => ServerActivityText.Count(StreamCount);

    /// <summary>One row per play method in use, in the web's order: direct, remux, direct stream, transcode, unknown.</summary>
    public ObservableCollection<ActivityStreamRow> Streams { get; } = [];

    /// <summary>The server offers its scheduled tasks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTasks))]
    public partial bool ShowsTasks { get; private set; }

    /// <summary>The tasks running now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTasks), nameof(HasNoTasks), nameof(TaskCountText))]
    public partial int TaskCount { get; private set; }

    public bool HasTasks => TaskCount > 0;

    /// <summary>"No running tasks" in place of the rows.</summary>
    public bool HasNoTasks => ShowsTasks && !HasTasks;

    public string TaskCountText => ServerActivityText.Count(TaskCount);

    /// <summary>A row per running task, in the server's order.</summary>
    public ObservableCollection<ActivityTaskRow> Tasks { get; } = [];

    /// <summary>The server gave the scans' channel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoScans))]
    public partial bool ShowsScans { get; private set; }

    /// <summary>The scans queued or running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScans), nameof(HasNoScans), nameof(ScanCountText))]
    public partial int ScanCount { get; private set; }

    public bool HasScans => ScanCount > 0;

    public bool HasNoScans => ShowsScans && !HasScans;

    public string ScanCountText => ServerActivityText.Count(ScanCount, IsCountLowerBound);

    /// <summary>The first <see cref="MaxScanRows"/> scans, in the stream's order.</summary>
    public ObservableCollection<ActivityScanRow> Scans { get; } = [];

    /// <summary>"3 more scans queued" under the rows, or "At least 475 more scans queued"; empty when every scan is listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreScans))]
    public partial string MoreScansText { get; private set; } = "";

    public bool HasMoreScans => MoreScansText.Length > 0;

    /// <summary>
    /// In place of the rows: "No active scans" when the stream said so. Without a live stream nobody knows, so the line
    /// names the connection instead ("Connecting…", "Disconnected"); and when the stream is live but could not say
    /// which scans there are, "Scans may be running", a text of Granary's own for a state the web does not have.
    /// </summary>
    [ObservableProperty]
    public partial string ScanEmptyText { get; private set; } = "";

    /// <summary>The server has a queue that prepares offline downloads.</summary>
    [ObservableProperty]
    public partial bool ShowsPreparations { get; private set; }

    /// <summary>The jobs still to finish: running, queued and waiting to retry. Paused and failed ones do not count.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreparations), nameof(PreparationCountText))]
    public partial int PreparationCount { get; private set; }

    public bool HasPreparations => PreparationCount > 0;

    public string PreparationCountText => ServerActivityText.Count(PreparationCount);

    /// <summary>The first <see cref="MaxPreparationRows"/> running jobs.</summary>
    public ObservableCollection<ActivityPreparationRow> Preparations { get; } = [];

    /// <summary>"2 more running · 4 queued · 1 waiting to retry"; empty when nothing waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaitingPreparations))]
    public partial string WaitingPreparationsText { get; private set; } = "";

    public bool HasWaitingPreparations => WaitingPreparationsText.Length > 0;

    /// <summary>"2 paused"; empty without any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPausedPreparations))]
    public partial string PausedPreparationsText { get; private set; } = "";

    public bool HasPausedPreparations => PausedPreparationsText.Length > 0;

    /// <summary>"1 failed in the last 24 hours"; empty without any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedPreparations), nameof(IsEmpty), nameof(HasSections))]
    public partial string FailedPreparationsText { get; private set; } = "";

    public bool HasFailedPreparations => FailedPreparationsText.Length > 0;

    /// <summary>"No downloads being prepared" in place of everything above.</summary>
    [ObservableProperty]
    public partial bool HasNoPreparations { get; private set; }

    /// <summary>"View all" beside Streams was picked: the shell opens Admin → Activity.</summary>
    public event EventHandler? StreamsRequested;

    [RelayCommand]
    private void ViewStreams() => StreamsRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _activity.Changed -= OnActivityChanged;
        _problem.Dispose();
        _pace.Dispose();
    }

    /// <summary>Shows <paramref name="state"/>; on the UI thread.</summary>
    internal void Show(ServerActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _showsAvailable = state.IsAvailable;
        IsAvailable = state.IsAvailable;
        ShowConnection(state);
        var sessions = state.Sessions ?? [];
        ShowsStreams = state.Sessions is not null;
        StreamCount = sessions.Count;
        ServerActivityRows.Sync(Streams,
            [.. sessions.GroupBy(AdminPlayMethod.Of).OrderBy(group => AdminPlayMethod.Rank(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal)],
            group => group.Key, row => row.Method, group => new ActivityStreamRow(group.Key), (row, group) => row.Count = group.Count());
        var running = (state.Tasks ?? []).Where(task => task.IsRunning).ToList();
        ShowsTasks = state.Tasks is not null;
        TaskCount = running.Count;
        ServerActivityRows.Sync(Tasks, running, task => task.Key, row => row.Key, task => new ActivityTaskRow(task.Key), (row, task) => row.Show(task));
        int scans = ShowScans(state);
        int preparations = ShowPreparations(state.Preparations);
        Total = sessions.Count + running.Count + scans + preparations;
    }

    // Answers how many scans count.
    private int ShowScans(ServerActivityState state)
    {
        var active = (state.Scans ?? []).Where(scan => scan.IsActive).ToList();
        ShowsScans = state.Channels.Contains(EventsChannels.Scans);
        IsCountLowerBound = ShowsScans && state.ScansMayBeMore;
        AreScansKnown = !ShowsScans || (state.Scans is not null && !IsCountLowerBound);
        ScanCount = active.Count;
        ServerActivityRows.Sync(Scans, [.. active.Take(MaxScanRows)], scan => scan.Id, row => row.Id, scan => new ActivityScanRow(scan.Id),
            (row, scan) => row.Show(scan, state.LibraryNames));
        // As the web: the scans in the stream's order, and every one past the listed ones in this line, whether it
        // waits or runs.
        int more = active.Count - Scans.Count;
        MoreScansText = more > 0 ? ServerActivityText.MoreScans(more, IsCountLowerBound) : "";
        ScanEmptyText = state.Connection == EventsConnection.Disconnected ? "Disconnected"
            : state.Connection != EventsConnection.Live ? "Connecting…"
            : IsCountLowerBound ? "Scans may be running"
            : state.Scans is not null ? "No active scans"
            : "Connecting…";
        return active.Count;
    }

    // As the web's PreparationsSummary. Answers how many jobs count.
    private int ShowPreparations(AdminDownloadPreparationList? list)
    {
        var counts = list?.Counts ?? new AdminDownloadPreparationCounts();
        var running = (list?.Items ?? []).Where(job => job.IsRunning).ToList();
        ShowsPreparations = list is not null;
        PreparationCount = counts.Active;
        ServerActivityRows.Sync(Preparations, [.. running.Take(MaxPreparationRows)], job => job.Id, row => row.Id,
            job => new ActivityPreparationRow(job.Id), (row, job) => row.Show(job));
        // Running jobs past the listed ones join the waiting line, so the lines still add up to the section's count. By
        // the queue's own count of them: the list is one page of at most 200 jobs (the web counts the page's).
        int moreRunning = Math.Max(counts.Running, running.Count) - Preparations.Count;
        WaitingPreparationsText = ServerActivityText.WaitingPreparations(moreRunning, counts.Queued, counts.Retrying);
        PausedPreparationsText = counts.Paused > 0 ? $"{ServerActivityText.Number(counts.Paused)} paused" : "";
        FailedPreparationsText = counts.FailedRecent > 0 ? $"{ServerActivityText.Number(counts.FailedRecent)} failed in the last 24 hours" : "";
        HasNoPreparations = list is not null && Preparations.Count == 0 && !HasWaitingPreparations && !HasPausedPreparations
            && !HasFailedPreparations;
        return counts.Active;
    }

    // The news comes on the stream's, a read's or a timer's thread, and a scan sends its progress as fast as it counts.
    // The first news is looked at at once and starts ShowInterval; more news within it waits for its end. Nothing here
    // depends on the posted look having run, so one the dispatcher dropped costs that look and no more.
    private void OnActivityChanged(object? sender, EventArgs e)
    {
        // Who may see the indicator at all (another profile, an access change) never waits.
        bool isUrgent = _activity.State.IsAvailable != _showsAvailable;
        lock (_paceGate)
        {
            if (_isPacing && !isUrgent)
            {
                _hasNews = true;
                return;
            }
            _isPacing = true;
            _hasNews = false;
            _pace.Change(ShowInterval, Timeout.InfiniteTimeSpan);
        }
        _dispatcher.Post(ShowLatest);
    }

    private void OnPaced()
    {
        lock (_paceGate)
        {
            if (!_hasNews)
            {
                _isPacing = false;
                return;
            }
            _hasNews = false;
            _pace.Change(ShowInterval, Timeout.InfiniteTimeSpan);
        }
        _dispatcher.Post(ShowLatest);
    }

    private void ShowLatest() => Show(_activity.State);

    // As the web's useDelayedConnectionProblem: the dot comes only once the stream stayed away, and goes at once.
    private void ShowConnection(ServerActivityState state)
    {
        ConnectionText = !state.IsAvailable ? "" : state.Connection switch
        {
            EventsConnection.Live => "",
            EventsConnection.Connecting => "Connecting…",
            _ => "Disconnected",
        };
        bool isOffline = state.IsAvailable && state.Connection != EventsConnection.Live;
        if (isOffline == _isOffline)
        {
            return;
        }
        _isOffline = isOffline;
        if (isOffline)
        {
            _problem.Change(ConnectionProblemDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _problem.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ShowsConnectionProblem = false;
        }
    }

    private void ShowConnectionProblem() => ShowsConnectionProblem = _isOffline;
}
