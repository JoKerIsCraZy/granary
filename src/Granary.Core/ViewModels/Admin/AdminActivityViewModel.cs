using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>A play method of the summary strip: its share of the streams, and whether it filters them.</summary>
public sealed record AdminMethodShare(string Method, int Count, double Fraction, bool IsSelected, bool IsDimmed)
{
    public string Label => AdminPlayMethod.Label(Method);
}

/// <summary>A routing node of the summary strip: how many streams pass it, and whether it filters them.</summary>
public sealed record AdminNodeShare(string Key, string Label, string Name, int Count, bool IsSelected, bool IsDimmed);

/// <summary>
/// Admin → Activity, as the web's (<c>pages/AdminActivity.tsx</c>): the streams running now with what reaches each viewer,
/// the share of each play method and routing node, a filter by viewer, title, client or node, Movies and Series, an
/// address lookup, and the commands a stream takes (pause, resume, stop, message, terminate). The web updates it live;
/// Granary reads it again every <see cref="RefreshEvery"/> while it shows.
/// </summary>
public sealed partial class AdminActivityViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    private readonly ISessionManager _session;
    private readonly AdminApi _admin;
    private readonly AdminSidebarViewModel _sidebar;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminActivityViewModel> _logger;
    private readonly Dictionary<string, AdminSessionRow> _rows = new(StringComparer.Ordinal);
    private CancellationTokenSource _run = new();
    private object? _loadedFor;
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset? _attemptedAt;
    private bool _loading;

    public AdminActivityViewModel(ISessionManager session, AdminApi admin, AdminSidebarViewModel sidebar, INavigationService navigation,
        TimeProvider time, ILogger<AdminActivityViewModel> logger)
    {
        _session = session;
        _admin = admin;
        _sidebar = sidebar;
        _navigation = navigation;
        _time = time;
        _logger = logger;
    }

    /// <summary>Loading until the first read ends; Empty when nothing plays; Error when the read failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(HasStreams), nameof(SubtitleText), nameof(EmptyText),
        nameof(HasNoStreamsShown))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>The streams the filters let through, newest first.</summary>
    public ObservableCollection<AdminSessionRow> Streams { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AdminMethodShare> Methods { get; private set; } = [];

    /// <summary>The routing nodes; the strip shows them only with more than one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNodeChoice), nameof(SubtitleText))]
    public partial IReadOnlyList<AdminNodeShare> Nodes { get; private set; } = [];

    /// <summary>Filters by viewer, title, client, address or node.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>movie, series, or null for both.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoviesOnly), nameof(IsSeriesOnly))]
    public partial string? TypeFilter { get; private set; }

    [ObservableProperty]
    public partial string? MethodFilter { get; private set; }

    [ObservableProperty]
    public partial string? NodeFilter { get; private set; }

    /// <summary>"Showing 2 of 5 streams" while a search or filter narrows them; empty otherwise.</summary>
    [ObservableProperty]
    public partial string FilterStatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasFilters { get; private set; }

    /// <summary>What Refresh, a stream's command or the address lookup last said; empty when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    public string RefreshText => IsRefreshing ? "Refreshing..." : "Refresh";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool HasStreams => State == LibraryLoadState.Ready;

    public bool HasNodeChoice => Nodes.Count > 1;

    public bool IsMoviesOnly => TypeFilter == "movie";

    public bool IsSeriesOnly => TypeFilter == "series";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>"2 active streams across 1 node", or "No active streams".</summary>
    public string SubtitleText => _rows.Count == 0
        ? "No active streams"
        : $"{_rows.Count} active stream{(_rows.Count == 1 ? "" : "s")} across {Nodes.Count} node{(Nodes.Count == 1 ? "" : "s")}";

    /// <summary>Nothing to list: "No active streams", or "No streams match your filters".</summary>
    public string EmptyText => _rows.Count == 0 ? "No active streams" : "No streams match your filters";

    public bool HasNoStreamsShown => State is LibraryLoadState.Ready or LibraryLoadState.Empty && Streams.Count == 0;

    /// <summary>Shows the page: reads the streams for another server or profile, or when the last read is stale.</summary>
    public Task ActivateAsync()
    {
        if (Equals(_loadedFor, Key()) && State is LibraryLoadState.Ready or LibraryLoadState.Empty && !IsStale())
        {
            return Task.CompletedTask;
        }
        return LoadAsync(manual: false);
    }

    /// <summary>The page left: a read still running is dropped.</summary>
    public void Deactivate()
    {
        _run.Cancel();
        _loading = false;
        IsRefreshing = false;
    }

    /// <summary>
    /// The page's clock: the streams' times run, and <see cref="RefreshEvery"/> after the last read began the streams are
    /// read again, as the web's are kept live; never while a read still runs, and a failed one waits as long before the next.
    /// </summary>
    public Task TickAsync()
    {
        foreach (var row in _rows.Values)
        {
            row.Update(row.Session, _time.GetUtcNow());
        }
        return State is LibraryLoadState.Ready or LibraryLoadState.Empty && !_loading && IsDue() ? LoadAsync(manual: false) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(manual: true);

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand]
    private void ToggleMovies() => TypeFilter = TypeFilter == "movie" ? null : "movie";

    [RelayCommand]
    private void ToggleSeries() => TypeFilter = TypeFilter == "series" ? null : "series";

    [RelayCommand]
    private void ToggleMethod(AdminMethodShare? share) => MethodFilter = share is null || MethodFilter == share.Method ? null : share.Method;

    [RelayCommand]
    private void ToggleNode(AdminNodeShare? share) => NodeFilter = share is null || NodeFilter == share.Key ? null : share.Key;

    /// <summary>Clears the method, node and type filters; the search stays, as on the web.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        MethodFilter = null;
        NodeFilter = null;
        TypeFilter = null;
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void DismissNotice() => Notice = "";

    /// <summary>A stream's movie or series.</summary>
    [RelayCommand]
    private void OpenTitle(string? contentId)
    {
        if (!string.IsNullOrEmpty(contentId))
        {
            _navigation.NavigateTo(AppRoute.Item, contentId);
        }
    }

    partial void OnSearchTextChanged(string value) => ShowStreams();

    partial void OnTypeFilterChanged(string? value) => ShowStreams();

    partial void OnMethodFilterChanged(string? value) => ShowStreams();

    partial void OnNodeFilterChanged(string? value) => ShowStreams();

    private async Task LoadAsync(bool manual)
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        var key = Key();
        if (!Equals(_loadedFor, key))
        {
            _loadedFor = key;
            _updatedAt = null;
            _rows.Clear();
            Streams.Clear();
            State = LibraryLoadState.Loading;
        }
        IsRefreshing = manual;
        _loading = true;
        _attemptedAt = _time.GetUtcNow();
        try
        {
            var sessions = await _admin.ListSessionsAsync(server.BaseUri, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            Show(sessions.Items, server.BaseUri);
            _sidebar.Show(null, sessions.Items.Count);
            _updatedAt = _time.GetUtcNow();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load the playback sessions");
            if (State == LibraryLoadState.Loading)
            {
                ErrorText = ex is SiloApiException { Status: 401 or 403 }
                    ? "This account can't see the server's activity. Only administrators can."
                    : LibraryErrors.Describe(ex);
                State = LibraryLoadState.Error;
            }
            else if (manual)
            {
                // The streams shown stay; the next read tries again.
                Say(LibraryErrors.Describe(ex), isError: true);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // The page left, or another read took over; whatever a cancelled read threw is no failure.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _loading = false;
                IsRefreshing = false;
            }
        }
    }

    // The rows follow their sessions; a session that ended goes, one that started comes.
    private void Show(IReadOnlyList<AdminPlaybackSession> sessions, Uri server)
    {
        var now = _time.GetUtcNow();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions.Where(s => s.SessionId.Length > 0))
        {
            live.Add(session.SessionId);
            if (_rows.TryGetValue(session.SessionId, out var row))
            {
                row.Update(session, now);
            }
            else
            {
                _rows[session.SessionId] = new AdminSessionRow(session, server, now);
            }
        }
        foreach (string gone in _rows.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _rows.Remove(gone);
        }
        State = _rows.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        ShowShares();
        ShowStreams();
    }

    private void ShowShares()
    {
        int total = Math.Max(1, _rows.Count);
        Methods = [.. _rows.Values.GroupBy(row => row.Method)
            .OrderBy(group => MethodRank(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AdminMethodShare(group.Key, group.Count(), group.Count() / (double)total, MethodFilter == group.Key,
                MethodFilter is not null && MethodFilter != group.Key))];
        Nodes = [.. _rows.Values.SelectMany(row => AdminSessionText.Route(row.Session)).GroupBy(node => node.Key)
            .Select(group => (Node: group.First(), Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .Select(entry => new AdminNodeShare(entry.Node.Key, entry.Node.Label, entry.Node.Name, entry.Count, NodeFilter == entry.Node.Key,
                NodeFilter is not null && NodeFilter != entry.Node.Key))];
        if (MethodFilter is not null && Methods.All(share => share.Method != MethodFilter))
        {
            MethodFilter = null;
        }
        if (NodeFilter is not null && Nodes.All(share => share.Key != NodeFilter))
        {
            NodeFilter = null;
        }
    }

    // Filtered and newest first, moving the rows already listed rather than listing them anew.
    private void ShowStreams()
    {
        string query = SearchText.Trim();
        var shown = _rows.Values
            .Where(row => query.Length == 0 || row.Matches(query))
            .Where(row => MethodFilter is null || row.Method == MethodFilter)
            .Where(row => NodeFilter is null || AdminSessionText.Route(row.Session).Any(node => node.Key == NodeFilter))
            .Where(row => TypeFilter is null || row.MediaType == TypeFilter)
            .OrderByDescending(row => row.Session.StartedAt)
            .ToList();
        for (int i = Streams.Count - 1; i >= 0; i--)
        {
            if (!shown.Contains(Streams[i]))
            {
                Streams.RemoveAt(i);
            }
        }
        for (int i = 0; i < shown.Count; i++)
        {
            int at = Streams.IndexOf(shown[i]);
            if (at < 0)
            {
                Streams.Insert(i, shown[i]);
            }
            else if (at != i)
            {
                Streams.Move(at, i);
            }
        }
        HasFilters = MethodFilter is not null || NodeFilter is not null || TypeFilter is not null;
        FilterStatusText = query.Length > 0 || HasFilters ? $"Showing {shown.Count} of {_rows.Count} streams" : "";
        if (Methods.Any(share => share.IsSelected != (share.Method == MethodFilter)) || Nodes.Any(share => share.IsSelected != (share.Key == NodeFilter)))
        {
            ShowShares();
        }
        OnPropertyChanged(nameof(HasNoStreamsShown));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SubtitleText));
    }

    private void Say(string text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }

    private static int MethodRank(string method) => method switch
    {
        AdminPlayMethod.Direct => 0,
        AdminPlayMethod.Remux => 1,
        AdminPlayMethod.DirectStream => 2,
        AdminPlayMethod.Transcode => 3,
        AdminPlayMethod.Unknown => 4,
        _ => 5,
    };

    private bool IsDue() => _attemptedAt is not { } at || _time.GetUtcNow() - at >= RefreshEvery;

    private bool IsStale() => _updatedAt is not { } at || _time.GetUtcNow() - at >= RefreshEvery;

    private object Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
