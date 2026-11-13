using Granary.Core.Abstractions;
using Granary.Core.Downloads;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// Follows the active profile's downloads for a page while it shows (the Downloads page, a series' downloads). Changes come on
/// any thread and are shown on the UI thread, one refresh per burst: a change of any kind has the page read the downloads
/// again; transfers that only went further come by download, so the page reads just their bytes. It follows only between
/// <see cref="Attach"/> and <see cref="Detach"/>, so the downloads, which live as long as the app, never hold a page that left.
/// </summary>
internal sealed class DownloadsFollower
{
    private readonly DownloadManager _downloads;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<bool, IReadOnlyCollection<string>> _show;

    // What arrived since the last refresh: a change of any kind, or only transfers that went further, by download.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _progressed = new(StringComparer.Ordinal);
    private bool _attached;
    private bool _posted;
    private bool _changed;

    /// <param name="show">
    /// On the UI thread: whether anything but progress changed (then read everything again), and the downloads that went further.
    /// </param>
    public DownloadsFollower(DownloadManager downloads, IUiDispatcher dispatcher, Action<bool, IReadOnlyCollection<string>> show)
    {
        _downloads = downloads;
        _dispatcher = dispatcher;
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
            _progressed.Clear();
        }
        _downloads.Changed += OnChanged;
        return true;
    }

    /// <summary>Stops following: the downloads hold the page no longer.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _attached = false;
        }
        _downloads.Changed -= OnChanged;
    }

    /// <summary>Something else changed what the page shows, as a setting: everything again, once, on the UI thread.</summary>
    public void Poke() => OnChanged(this, EventArgs.Empty);

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
        string[] progressed;
        lock (_gate)
        {
            _posted = false;
            if (!_attached)
            {
                return;
            }
            changed = _changed;
            _changed = false;
            progressed = [.. _progressed];
            _progressed.Clear();
        }
        _show(changed, progressed);
    }
}
