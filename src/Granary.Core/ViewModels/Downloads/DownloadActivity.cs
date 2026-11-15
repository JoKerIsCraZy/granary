using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Downloads;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The active profile's downloads as the shell shows them outside their page (docs/architecture.md, "Downloads in the shell"):
/// the sidebar's count of the downloads coming or waiting (<see cref="DownloadTally.IsPending"/>) and whether one comes now. It
/// follows the downloads and the session for the app's whole run, from <see cref="Start"/>: changes come on any thread and are
/// shown on the UI thread, one refresh per burst, as the Downloads page's follower shows them. A transfer that only went further
/// changes neither.
/// </summary>
public sealed partial class DownloadActivity : ObservableObject, IDisposable
{
    /// <summary>The sidebar entry's name, and its start while downloads are in progress.</summary>
    public const string EntryTitle = "Downloads";

    private readonly DownloadManager _downloads;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;

    private readonly Lock _gate = new();
    private bool _started;
    private bool _disposed;
    private bool _posted;

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
        if (e is DownloadProgressedEventArgs)
        {
            return;
        }
        lock (_gate)
        {
            if (_disposed || _posted)
            {
                return;
            }
            _posted = true;
        }
        _dispatcher.Post(Refresh);
    }

    private void Refresh()
    {
        lock (_gate)
        {
            _posted = false;
            if (_disposed)
            {
                return;
            }
        }
        Show(DownloadTally.Of(_downloads.Items));
    }

    private void Show(DownloadTally tally)
    {
        PendingCount = tally.Pending;
        IsTransferring = tally.IsTransferring;
    }
}
