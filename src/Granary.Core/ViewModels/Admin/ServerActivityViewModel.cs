using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Realtime;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The activity indicator an acting administrator sees at the top right of every page, as the web's
/// (<c>components/ServerActivity.tsx</c>): a button with the number of things going on, and a panel that lists them. On
/// the admin area's pages it shows always, elsewhere only while something is going on or its panel is open. Its texts are
/// the web's.
/// </summary>
public sealed partial class ServerActivityViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the stream may be without a connection before the button says so, as the web waits.</summary>
    public static readonly TimeSpan ConnectionProblemDelay = TimeSpan.FromSeconds(4);

    private readonly ServerActivity _activity;
    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _problem;
    private int _posted;
    private bool _isOffline;

    public ServerActivityViewModel(ServerActivity activity, IUiDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _activity = activity;
        _dispatcher = dispatcher;
        _problem = time.CreateTimer(_ => _dispatcher.Post(ShowConnectionProblem), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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

    public bool IsVisible => IsAvailable && (ShowsWhenIdle || Total > 0 || IsPanelOpen);

    /// <summary>Streams, running tasks, active scans and downloads being prepared, together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(HasActivity), nameof(BadgeText), nameof(Name), nameof(IsEmpty), nameof(HasSections))]
    public partial int Total { get; private set; }

    public bool HasActivity => Total > 0;

    /// <summary>The red count on the button: "3", or "99+".</summary>
    public string BadgeText => ServerActivityText.Count(Total);

    /// <summary>What a screen reader calls the button.</summary>
    public string Name => Total > 0 ? $"Server activity: {Total} active" : "Server activity";

    /// <summary>The stream has had no connection for <see cref="ConnectionProblemDelay"/>: a red dot on the button.</summary>
    [ObservableProperty]
    public partial bool ShowsConnectionProblem { get; private set; }

    /// <summary>"Connecting…" or "Disconnected" in the panel's header; empty while the stream is live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionText))]
    public partial string ConnectionText { get; private set; } = "";

    public bool HasConnectionText => ConnectionText.Length > 0;

    /// <summary>Nothing is going on: the panel says so in place of its sections.</summary>
    public bool IsEmpty => Total == 0;

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

    /// <summary>"View all" beside Streams was picked: the shell opens Admin → Activity.</summary>
    public event EventHandler? StreamsRequested;

    [RelayCommand]
    private void ViewStreams() => StreamsRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _activity.Changed -= OnActivityChanged;
        _problem.Dispose();
    }

    /// <summary>Shows <paramref name="state"/>; on the UI thread.</summary>
    internal void Show(ServerActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
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
        Total = sessions.Count + running.Count;
    }

    // The news comes on the stream's or a read's thread, in bursts: one look at the latest state per burst.
    private void OnActivityChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0)
        {
            _dispatcher.Post(() =>
            {
                Volatile.Write(ref _posted, 0);
                Show(_activity.State);
            });
        }
    }

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
