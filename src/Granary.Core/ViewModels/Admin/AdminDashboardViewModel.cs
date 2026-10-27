using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Admin → Dashboard, as the web's opens on a server without a layout of its own (<c>pages/AdminDashboard.tsx</c>,
/// <c>DEFAULT_LAYOUT_ORDER</c>): the number tiles, the health strip, Now Playing, playback and egress over the last
/// 24 hours, the week's top titles and profiles, the libraries, the users and the recent activity. Each part shows
/// its own failure, as each web widget does. Granary only reads: no scans, no layout to customize. While the page
/// shows, <see cref="TickAsync"/> reads everything again once a minute, as the web polls. What it reads of the build and
/// the sessions goes to the admin sidebar too (<see cref="AdminSidebarViewModel"/>).
/// </summary>
public sealed partial class AdminDashboardViewModel : ObservableObject
{
    /// <summary>How often a shown dashboard reads itself again.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    internal const int ChartHours = 24;
    internal const int TopDays = 7;
    internal const int UsersRead = 200;

    private readonly ISessionManager _session;
    private readonly AdminApi _admin;
    private readonly AdminSidebarViewModel _sidebar;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminDashboardViewModel> _logger;
    private CancellationTokenSource _run = new();
    private object? _loadedFor;
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset? _attemptedAt;
    private bool _loading;

    public AdminDashboardViewModel(ISessionManager session, AdminApi admin, AdminSidebarViewModel sidebar, INavigationService navigation,
        TimeProvider time, ILogger<AdminDashboardViewModel> logger)
    {
        _session = session;
        _admin = admin;
        _sidebar = sidebar;
        _navigation = navigation;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Raised by Now Playing's and Recent Activity's links: the admin area shows its Activity page, as one place in the
    /// history, so Back to App still leaves it.
    /// </summary>
    public event EventHandler? ActivityRequested;

    /// <summary>Loading until the first read ends; Error when the account may not read the dashboard at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsShown), nameof(HasError))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    /// <summary>A Refresh asked for is running: the button says "Refreshing..." and waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>"Updated less than 1 minute ago"; empty before the first read.</summary>
    [ObservableProperty]
    public partial string UpdatedText { get; private set; } = "";

    public string RefreshText => IsRefreshing ? "Refreshing..." : "Refresh";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsShown => State == LibraryLoadState.Ready;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>
    /// Shows the page: reads everything for another server or profile, again when the last read is older than
    /// <see cref="RefreshEvery"/>, and leaves a fresh one as it is.
    /// </summary>
    public Task ActivateAsync()
    {
        if (Equals(_loadedFor, Key()) && State == LibraryLoadState.Ready && !IsStale())
        {
            ShowUpdated();
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
    /// The page's clock: "Updated …" moves on, and <see cref="RefreshEvery"/> after the last read began the dashboard reads
    /// itself again; never while a read still runs, and a failed one waits as long before the next.
    /// </summary>
    public Task TickAsync()
    {
        ShowUpdated();
        return State == LibraryLoadState.Ready && !_loading && IsDue() ? LoadAsync(manual: false) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(manual: true);

    private bool CanRefresh() => !IsRefreshing;

    /// <summary>Now Playing's and Recent Activity's way to the Activity page.</summary>
    [RelayCommand]
    private void OpenActivity() => ActivityRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>A stream's or a top title's movie or series.</summary>
    [RelayCommand]
    private void OpenTitle(string? contentId)
    {
        if (!string.IsNullOrEmpty(contentId))
        {
            _navigation.NavigateTo(AppRoute.Item, contentId);
        }
    }

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
            // Another server's or profile's numbers never show under this one's.
            _loadedFor = key;
            _updatedAt = null;
            UpdatedText = "";
            State = LibraryLoadState.Loading;
        }
        IsRefreshing = manual;
        _loading = true;
        _attemptedAt = _time.GetUtcNow();
        try
        {
            var read = await ReadAsync(server.BaseUri, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            if (read.Stats.Error is SiloApiException { Status: 401 or 403 })
            {
                ErrorText = "This account can't open the server's dashboard. Only administrators can.";
                State = LibraryLoadState.Error;
                return;
            }
            Show(read, server.BaseUri, _time.GetUtcNow());
            // The admin sidebar's build and live count need no reads of their own.
            _sidebar.Show(read.Build.Value, read.Sessions.Value?.Items.Count);
            _updatedAt = _time.GetUtcNow();
            ShowUpdated();
            State = LibraryLoadState.Ready;
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

    // Every source at once; each failure stays its own part's.
    private async Task<DashboardRead> ReadAsync(Uri server, CancellationToken token)
    {
        var stats = FetchAsync(t => _admin.GetStatsAsync(server, t), "stats", token);
        var sessions = FetchAsync(t => _admin.ListSessionsAsync(server, t), "sessions", token);
        var status = FetchAsync(t => _admin.GetServerStatusAsync(server, t), "server status", token);
        var build = FetchAsync(t => _admin.GetBuildAsync(server, t), "build", token);
        var nodes = FetchAsync(t => _admin.ListNodesAsync(server, t), "nodes", token);
        var egress = FetchAsync(t => _admin.GetTimeseriesAsync(server, ChartHours, t), "egress history", token);
        var playback = FetchAsync(t => _admin.GetPlaybackActivityAsync(server, ChartHours, t), "playback activity", token);
        var top = FetchAsync(t => _admin.GetTopActivityAsync(server, TopDays, t), "top activity", token);
        var users = FetchAsync(t => _admin.ListUsersAsync(server, UsersRead, t), "users", token);
        var libraries = FetchAsync(t => _admin.ListLibrariesAsync(server, t), "libraries", token);
        await Task.WhenAll(stats, sessions, status, build, nodes, egress, playback, top, users, libraries);
        return new DashboardRead(await stats, await sessions, await status, await build, await nodes, await egress, await playback,
            await top, await users, await libraries);
    }

    private async Task<Fetched<T>> FetchAsync<T>(Func<CancellationToken, Task<T>> call, string what, CancellationToken token)
        where T : class
    {
        try
        {
            return new Fetched<T>(await call(token), null);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load the dashboard's {Part}", what);
            return new Fetched<T>(null, ex);
        }
    }

    private bool IsDue() => _attemptedAt is not { } at || _time.GetUtcNow() - at >= RefreshEvery;

    private bool IsStale() => _updatedAt is not { } at || _time.GetUtcNow() - at >= RefreshEvery;

    private void ShowUpdated() => UpdatedText = _updatedAt is { } at ? AdminText.Updated(at, _time.GetUtcNow()) : "";

    private object Key() => (_session.ActiveServer?.Id, _session.ProfileId);

    /// <summary>A source's answer, or why there is none.</summary>
    private readonly record struct Fetched<T>(T? Value, Exception? Error)
        where T : class;

    private sealed record DashboardRead(
        Fetched<AdminDashboardStats> Stats,
        Fetched<AdminPlaybackSessionCollection> Sessions,
        Fetched<AdminServerStatus> Status,
        Fetched<AdminBuildInfo> Build,
        Fetched<AdminNodeCollection> Nodes,
        Fetched<AdminDashboardTimeseries> Egress,
        Fetched<AdminDashboardPlaybackActivity> Playback,
        Fetched<AdminDashboardTopActivity> Top,
        Fetched<AdminUserCollection> Users,
        Fetched<AdminLibraryCollection> Libraries);
}
