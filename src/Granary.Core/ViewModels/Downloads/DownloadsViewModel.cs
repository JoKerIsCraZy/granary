using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// A part of the Downloads page: the films, or one series with its poster (empty when there is none, never null, as an
/// x:Bind function skips a null argument).
/// </summary>
public sealed record DownloadSection(string Title, string PosterPath, IReadOnlyList<DownloadGroup> Groups);

/// <summary>A season of a series ("Season 1", "Specials"), or the films, whose one group has no title.</summary>
public sealed record DownloadGroup(string Title, IReadOnlyList<DownloadRowViewModel> Rows);

/// <summary>
/// The Downloads page: the active profile's downloads on this PC, as the Apple app lists them, films by title, then each series
/// by title with its seasons and episodes by number and Specials last; the space they use and the space left at the top. The
/// web has no such page, so its words are the apps'. It follows the downloads between <see cref="Attach"/> and
/// <see cref="Detach"/>: changes come on any thread and are shown on the UI thread, one refresh per burst. A transfer that only
/// went further updates its row and the space used without reading the other downloads. Rows follow their download's state
/// themselves; the sections are made again only when downloads come, go or move, so a row and its buttons stay put while the
/// viewer pauses or resumes it. The free space is read off the UI thread, when the page shows and when a download comes,
/// goes or finishes; a read that fails or takes longer than <see cref="DiskSpaceReads.Timeout"/> leaves it unknown, and the
/// top then says only the space used. Above the list it suggests deleting the watched downloads to free up space, as both
/// apps do (the Apple app's "Free up 3.0 GB · 2 watched", the Android app's question), unless Settings → Downloads keeps
/// watched downloads; nothing is deleted without the question.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    public const string EmptyTitle = "No downloads yet";
    public const string EmptyText = "Choose Download in a film's or episode's ⋯ menu to keep it on this PC and watch it without your Silo server.";
    public const string FilmsTitle = "Films";
    public const string SpecialsTitle = "Specials";
    public const string ReclaimTitle = "Free up space?";
    public const string ReclaimAction = "Delete";

    private static readonly StringComparer TitleOrder = StringComparer.CurrentCultureIgnoreCase;

    private readonly DownloadManager _downloads;
    private readonly IDiskSpace _disk;
    private readonly IDownloadPreferences _preferences;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadsViewModel> _logger;

    // What arrived since the last refresh: a change of any kind, or only transfers that went further, by download.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _progressed = new(StringComparer.Ordinal);
    private bool _attached;
    private bool _posted;
    private bool _changed;

    // On the UI thread: one row per download, what the sections were made from, and the space as last known.
    private readonly Dictionary<string, DownloadRowViewModel> _rows = new(StringComparer.Ordinal);
    private List<Shape> _shapes = [];
    private int _completed;
    private IReadOnlyList<DownloadItem> _reclaimable = [];
    private long _used;
    private long? _free;
    private int _reads;

    public DownloadsViewModel(DownloadManager downloads, IDiskSpace disk, IDownloadPreferences preferences, INavigationService navigation,
        IConfirmation confirmation, IUiDispatcher dispatcher, TimeProvider time, ILogger<DownloadsViewModel> logger)
    {
        _downloads = downloads;
        _disk = disk;
        _preferences = preferences;
        _navigation = navigation;
        _confirmation = confirmation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>"12.0 GB used · 200.0 GB free"; only the space used while the free space is not known.</summary>
    [ObservableProperty]
    public partial string SpaceText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<DownloadSection> Sections { get; private set; } = [];

    /// <summary>The profile has no downloads on this PC: the page says how to get some (<see cref="EmptyTitle"/>, <see cref="EmptyText"/>).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// The profile has watched downloads on this PC and Settings → Downloads does not keep them: the page suggests deleting
    /// them (<see cref="ReclaimText"/>, <see cref="ReclaimCommand"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReclaimCommand))]
    public partial bool ShowsReclaim { get; private set; }

    /// <summary>"Free up 3.0 GB · 2 watched", the Apple app's line; empty without a suggestion.</summary>
    [ObservableProperty]
    public partial string ReclaimText { get; private set; } = "";

    /// <summary>The last read of the free space; done once <see cref="SpaceText"/> shows it.</summary>
    public Task SpaceRead { get; private set; } = Task.CompletedTask;

    /// <summary>The page shows, on the UI thread: it lists the downloads and follows them until <see cref="Detach"/>.</summary>
    public void Attach()
    {
        lock (_gate)
        {
            if (_attached)
            {
                return;
            }
            _attached = true;
            // A refresh posted before the page was left may never have run.
            _posted = false;
            _changed = false;
            _progressed.Clear();
        }
        _downloads.Changed += OnChanged;
        // Keep watched downloads, switched in Settings while the page stays in memory, shows at once.
        _preferences.Changed += OnChanged;
        Show(readSpace: true);
    }

    /// <summary>
    /// Asks whether the watched downloads may go, in the Android app's words, and deletes them on a yes: the ones the question
    /// counted that are still watched then.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ShowsReclaim))]
    private async Task ReclaimAsync()
    {
        var asked = _reclaimable;
        if (asked.Count == 0)
        {
            return;
        }
        string count = asked.Count.ToString(CultureInfo.InvariantCulture);
        string message = asked.Count == 1
            ? $"Delete 1 watched download and free {Size(asked.Sum(i => i.ExpectedBytes))}?"
            : $"Delete {count} watched downloads and free {Size(asked.Sum(i => i.ExpectedBytes))}?";
        if (await _confirmation.AskAsync(ReclaimTitle, message, ReclaimAction))
        {
            await _downloads.DeleteWatchedAsync([.. asked.Select(i => i.DownloadId)], CancellationToken.None);
        }
    }

    /// <summary>The page was left: it stops following the downloads, which hold it no longer.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _attached = false;
        }
        _downloads.Changed -= OnChanged;
        _preferences.Changed -= OnChanged;
    }

    public void Dispose() => Detach();

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
        if (changed)
        {
            Show(readSpace: false);
        }
        else
        {
            ShowProgress(progressed);
        }
    }

    // Everything again: the rows brought up to date, the sections made again only when downloads came, went or moved. A state
    // change is the row's own: rebuilding for it would take the focus off the button just pressed.
    private void Show(bool readSpace)
    {
        var items = _downloads.Items;
        var shapes = items.Select(Shape.Of).OrderBy(s => s.DownloadId, StringComparer.Ordinal).ToList();
        bool rebuild = readSpace || !shapes.SequenceEqual(_shapes);
        _shapes = shapes;
        int completed = items.Count(i => i.State == DownloadState.Completed);
        // A download that came, went or finished changed what the drive has free.
        bool reread = rebuild || completed != _completed;
        _completed = completed;
        foreach (var item in items)
        {
            if (_rows.TryGetValue(item.DownloadId, out var row))
            {
                row.Update(item);
            }
            else
            {
                _rows[item.DownloadId] = new DownloadRowViewModel(item, _downloads, _navigation, _confirmation);
            }
        }
        foreach (string gone in _rows.Keys.Except(items.Select(i => i.DownloadId), StringComparer.Ordinal).ToList())
        {
            _rows.Remove(gone);
        }
        if (rebuild)
        {
            Sections = Arrange(items);
            IsEmpty = Sections.Count == 0;
        }
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

    // What the suggestion counts: the watched downloads that are whole on this PC, unless the viewer keeps them.
    private void ShowReclaim(IReadOnlyList<DownloadItem> items)
    {
        _reclaimable = _preferences.KeepWatched ? [] : [.. items.Where(i => i.IsReclaimable)];
        ReclaimText = _reclaimable.Count == 0
            ? ""
            : $"Free up {Size(_reclaimable.Sum(i => i.ExpectedBytes))} · {_reclaimable.Count.ToString(CultureInfo.InvariantCulture)} watched";
        ShowsReclaim = _reclaimable.Count > 0;
    }

    // Transfers that went further: only their rows and the space used.
    private void ShowProgress(IReadOnlyList<string> downloadIds)
    {
        foreach (string id in downloadIds)
        {
            if (_rows.TryGetValue(id, out var row))
            {
                row.Update(row.Item with { BytesDone = _downloads.BytesDone(id) });
            }
        }
        _used = _downloads.BytesUsed();
        ShowSpace();
    }

    // Films by title in one untitled group, then each series by title, its seasons by number with Specials (0, or none) last,
    // and their episodes by number.
    private List<DownloadSection> Arrange(IReadOnlyList<DownloadItem> items)
    {
        var sections = new List<DownloadSection>();
        var films = items.Where(i => i.SeriesId is null).OrderBy(i => i.Title, TitleOrder).ThenBy(i => i.DownloadId, StringComparer.Ordinal)
            .Select(i => _rows[i.DownloadId]).ToList();
        if (films.Count > 0)
        {
            sections.Add(new DownloadSection(FilmsTitle, "", [new DownloadGroup("", films)]));
        }
        var series = items.Where(i => i.SeriesId is not null).GroupBy(i => i.SeriesId!, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Title: DownloadRowViewModel.SeriesTitleOf(g.First()), Episodes: g.ToList()))
            .OrderBy(s => s.Title, TitleOrder).ThenBy(s => s.Id, StringComparer.Ordinal);
        foreach (var (_, title, episodes) in series)
        {
            var seasons = episodes.GroupBy(i => i.SeasonNumber is int number and > 0 ? number : 0)
                .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
                .Select(g => new DownloadGroup(g.Key == 0 ? SpecialsTitle : "Season " + g.Key.ToString(CultureInfo.InvariantCulture),
                    [.. g.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Title, TitleOrder).Select(i => _rows[i.DownloadId])]))
                .ToList();
            sections.Add(new DownloadSection(title, episodes.Select(i => i.SeriesPosterPath).FirstOrDefault(p => p is not null) ?? "", seasons));
        }
        return sections;
    }

    // Windows can take its time over a drive that sleeps or a share: the free space is read on the thread pool and shown on the UI thread.
    private void ReadSpace()
    {
        int read = ++_reads;
        ShowSpace();
        SpaceRead = ReadSpaceAsync(_preferences.Folder, read);
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
        string used = Size(_used) + " used";
        SpaceText = _free is { } free ? $"{used} · {Size(free)} free" : used;
    }

    private static string Size(long bytes) => bytes > 0 ? PrePlayText.FileSize(bytes) : "0 B";

    // What the sections are made from: a download's place on the page and its series' poster. Its state, its progress and the
    // artwork a row shows itself are the row's.
    private sealed record Shape(string DownloadId, string Title, string? SeriesId, string? SeriesTitle, int? SeasonNumber, int? EpisodeNumber,
        string? SeriesPosterPath)
    {
        public static Shape Of(DownloadItem item) => new(item.DownloadId, item.Title, item.SeriesId, item.SeriesTitle, item.SeasonNumber,
            item.EpisodeNumber, item.SeriesId is null ? null : item.SeriesPosterPath);
    }
}
