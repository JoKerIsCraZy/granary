using Granary.Core.Abstractions;
using Granary.Core.Downloads;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// Follows the active profile's downloads for a page while it shows (the Downloads page, a series' downloads). Changes come on
/// any thread and are shown on the UI thread, one refresh per burst: a change of any kind has the page read the downloads
/// again; transfers that only went further come by download, so the page reads just their bytes. While a download the page
/// shows comes, its speed is read once every <see cref="RateInterval"/> (<see cref="RateOf"/>), as the Apple app's row
/// refreshes once a second: often enough to follow it, and the text does not flicker with every read of the network; a
/// speed that stopped is noticed then too, when no progress comes. It follows only between <see cref="Attach"/> and
/// <see cref="Detach"/>, so the downloads, which live as long as the app, never hold a page that left, nor does the clock.
/// </summary>
internal sealed class DownloadsFollower
{
    /// <summary>How often the speed of a download that comes is read again.</summary>
    public static readonly TimeSpan RateInterval = TimeSpan.FromSeconds(1);

    private readonly DownloadManager _downloads;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Action<bool, IReadOnlyCollection<string>> _show;

    // What arrived since the last refresh: a change of any kind, transfers that went further by download, or the clock's tick.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _progressed = new(StringComparer.Ordinal);
    private bool _attached;
    private bool _posted;
    private bool _changed;
    private bool _ticked;
    private ITimer? _ticker;

    // On the UI thread: the downloads the page shows coming, and their speeds as last read.
    private HashSet<string> _coming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _rates = new(StringComparer.Ordinal);

    /// <param name="show">
    /// On the UI thread: whether anything but progress changed (then read everything again), and the downloads that went further
    /// or whose speed was read again.
    /// </param>
    public DownloadsFollower(DownloadManager downloads, IUiDispatcher dispatcher, TimeProvider time, Action<bool, IReadOnlyCollection<string>> show)
    {
        _downloads = downloads;
        _dispatcher = dispatcher;
        _time = time;
        _show = show;
    }

    /// <summary>Between <see cref="Attach"/> and <see cref="Detach"/>; any thread.</summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _attached;
            }
        }
    }

    /// <summary>Starts following; false when it already follows. The page shows everything itself then.</summary>
    public bool Attach()
    {
        lock (_gate)
        {
            if (_attached)
            {
                return false;
            }
            _attached = true;
            // A refresh posted before the page was left may never have run.
            _posted = false;
            _changed = false;
            _ticked = false;
            _progressed.Clear();
        }
        // Speeds read before the page was left are no speeds now (UI thread, as Track and Refresh).
        _coming.Clear();
        _rates.Clear();
        _downloads.Changed += OnChanged;
        return true;
    }

    /// <summary>Stops following: the downloads and the clock hold the page no longer.</summary>
    public void Detach()
    {
        ITimer? ticker;
        lock (_gate)
        {
            _attached = false;
            ticker = _ticker;
            _ticker = null;
        }
        _downloads.Changed -= OnChanged;
        ticker?.Dispose();
    }

    /// <summary>Something else changed what the page shows, as a setting: everything again, once, on the UI thread.</summary>
    public void Poke() => OnChanged(this, EventArgs.Empty);

    /// <summary>
    /// On the UI thread, after each refresh: the downloads the page shows that come now. Their speed is read once a second while
    /// any comes; the clock stops when none does.
    /// </summary>
    public void Track(IEnumerable<DownloadItem> shown)
    {
        _coming = shown.Where(i => i.State == DownloadState.Downloading).Select(i => i.DownloadId).ToHashSet(StringComparer.Ordinal);
        foreach (string gone in _rates.Keys.Where(id => !_coming.Contains(id)).ToList())
        {
            _rates.Remove(gone);
        }
        ITimer? stopped = null;
        lock (_gate)
        {
            if (!_attached)
            {
                return;
            }
            if (_coming.Count > 0)
            {
                _ticker ??= _time.CreateTimer(static state => ((DownloadsFollower)state!).OnTick(), this, RateInterval, RateInterval);
            }
            else
            {
                stopped = _ticker;
                _ticker = null;
            }
        }
        stopped?.Dispose();
    }

    /// <summary>A download's speed as last read, in bytes a second; null before it was read, and while it is not known.</summary>
    public double? RateOf(string downloadId) => _rates.TryGetValue(downloadId, out double rate) ? rate : null;

    // The clock's thread.
    private void OnTick()
    {
        lock (_gate)
        {
            if (!_attached)
            {
                return;
            }
            _ticked = true;
            if (_posted)
            {
                return;
            }
            _posted = true;
        }
        _dispatcher.Post(Refresh);
    }

    // Any thread. One refresh is posted at a time; what came meanwhile waits for it.
    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_attached)
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
        bool ticked;
        HashSet<string> progressed;
        lock (_gate)
        {
            _posted = false;
            if (!_attached)
            {
                return;
            }
            changed = _changed;
            ticked = _ticked;
            _changed = false;
            _ticked = false;
            progressed = new HashSet<string>(_progressed, StringComparer.Ordinal);
            _progressed.Clear();
        }
        if (ticked)
        {
            foreach (string id in _coming)
            {
                if (_downloads.BytesPerSecond(id) is { } rate)
                {
                    _rates[id] = rate;
                }
                else
                {
                    _rates.Remove(id);
                }
                progressed.Add(id);
            }
        }
        _show(changed, progressed);
    }
}
