using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Downloads;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The active profile's downloads as the shell shows them outside their page (docs/architecture.md, "Downloads in the shell"):
/// the sidebar's count of the downloads coming or waiting (<see cref="DownloadTally.IsPending"/>), whether one comes now, and
/// the taskbar button's progress. It follows the downloads and the session for the app's whole run, from <see cref="Start"/>:
/// changes come on any thread and are shown on the UI thread, one refresh per burst, as the Downloads page's follower shows
/// them. A transfer that only went further updates its bytes without reading every download again.
/// </summary>
public sealed partial class DownloadActivity : ObservableObject, IDisposable
{
    /// <summary>The sidebar entry's name, and its start while downloads are in progress.</summary>
    public const string EntryTitle = "Downloads";

    private readonly DownloadManager _downloads;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;

    // What arrived since the last refresh: a change of any kind, or transfers that went further, by download.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _progressed = new(StringComparer.Ordinal);
    private bool _started;
    private bool _disposed;
    private bool _posted;
    private bool _changed;

    // On the UI thread: the downloads as last read.
    private IReadOnlyList<DownloadItem> _items = [];

    public DownloadActivity(DownloadManager downloads, ISessionManager session, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _downloads = downloads;
        _session = session;
        _dispatcher = dispatcher;
    }

    /// <summary>How many of the active profile's downloads are coming or waiting: not paused, failed or done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(EntryName))]
    public partial int PendingCount { get; private set; }

    public bool HasPending => PendingCount > 0;

    /// <summary>One of them comes now: its bytes arrive, nothing pauses or holds it.</summary>
    [ObservableProperty]
    public partial bool IsTransferring { get; private set; }

    /// <summary>The downloads' overall progress, as the taskbar button shows it.</summary>
    [ObservableProperty]
    public partial DownloadProgress Progress { get; private set; } = DownloadProgress.None;

    /// <summary>
    /// The entry as a screen reader names it: "Downloads", or "Downloads, 3 downloads in progress" in the Apple app's words for
    /// downloads still coming.
    /// </summary>
    public string EntryName => PendingCount switch
    {
        0 => EntryTitle,
        1 => $"{EntryTitle}, 1 download in progress",
        _ => $"{EntryTitle}, {PendingCount.ToString(CultureInfo.InvariantCulture)} downloads in progress",
    };

    /// <summary>Starts following, and reads the downloads once on the UI thread. Once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }
            _started = true;
        }
        _downloads.Changed += OnChanged;
        _session.Changed += OnSessionChanged;
        OnChanged(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _downloads.Changed -= OnChanged;
        _session.Changed -= OnSessionChanged;
    }

    // Another profile has other downloads.
    private void OnSessionChanged(object? sender, EventArgs e) => OnChanged(sender, EventArgs.Empty);

    // Any thread. One refresh is posted at a time; what comes meanwhile waits for it.
    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (e is DownloadProgressedEventArgs progressed)
            {
                _progressed.Add(progressed.DownloadId);
            }
            else
            {
                _changed = true;
            }
            if (_posted)
            {
                return;
            }
            _posted = true;
        }
        _dispatcher.Post(Refresh);
    }

    private void Refresh()
    {
        bool changed;
        HashSet<string> progressed;
        lock (_gate)
        {
            _posted = false;
            if (_disposed)
            {
                return;
            }
            changed = _changed;
            _changed = false;
            progressed = new HashSet<string>(_progressed, StringComparer.Ordinal);
            _progressed.Clear();
        }
        if (changed)
        {
            _items = _downloads.Items;
        }
        else if (progressed.Count > 0)
        {
            _items = [.. _items.Select(i => progressed.Contains(i.DownloadId) ? i with { BytesDone = _downloads.BytesDone(i.DownloadId) } : i)];
        }
        Show(DownloadTally.Of(_items));
    }

    private void Show(DownloadTally tally)
    {
        PendingCount = tally.Pending;
        IsTransferring = tally.IsTransferring;
        Progress = tally.Progress;
    }
}
