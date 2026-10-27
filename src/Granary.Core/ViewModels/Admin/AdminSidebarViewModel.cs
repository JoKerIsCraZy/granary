using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The admin area's own sidebar, as the web's (<c>components/AdminSidebar.tsx</c>): Activity's "2 live" and the server's
/// build at its foot. The dashboard hands it what it read; on its own it reads what is older than <see cref="FreshFor"/>.
/// </summary>
public sealed partial class AdminSidebarViewModel : ObservableObject
{
    /// <summary>How long a live count is taken as current.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _session;
    private readonly AdminApi _admin;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminSidebarViewModel> _logger;
    private object? _shownFor;
    private bool _hasBuild;
    private DateTimeOffset _countedAt = DateTimeOffset.MinValue;

    public AdminSidebarViewModel(ISessionManager session, AdminApi admin, TimeProvider time, ILogger<AdminSidebarViewModel> logger)
    {
        _session = session;
        _admin = admin;
        _time = time;
        _logger = logger;
    }

    /// <summary>"1715 · 13ad7a74", "dev build" for a server without build info, "loading..." or "load failed".</summary>
    [ObservableProperty]
    public partial string BuildText { get; private set; } = "loading...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLive), nameof(LiveText))]
    public partial int LiveCount { get; private set; }

    public bool HasLive => LiveCount > 0;

    /// <summary>The admin page last shown (<see cref="AdminPages"/>), so Back into the area returns to it.</summary>
    public string LastPage { get; set; } = AdminPages.Dashboard;

    /// <summary>"2 live" beside Activity while anything plays.</summary>
    public string LiveText => $"{LiveCount} live";

    /// <summary>Reads the build once for the server and profile, and the live count when it is stale.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        Follow();
        var key = _shownFor;
        var build = _hasBuild ? Task.CompletedTask : ReadBuildAsync(server.BaseUri, key, cancellationToken);
        var live = _time.GetUtcNow() - _countedAt < FreshFor ? Task.CompletedTask : ReadLiveAsync(server.BaseUri, key, cancellationToken);
        await Task.WhenAll(build, live);
    }

    /// <summary>What a page read itself: the build, and the sessions running now. Null leaves a part as it is.</summary>
    public void Show(AdminBuildInfo? build, int? live)
    {
        Follow();
        if (build is not null)
        {
            ShowBuild(build);
        }
        if (live is { } count)
        {
            LiveCount = count;
            _countedAt = _time.GetUtcNow();
        }
    }

    // Another server's or profile's build and count never show under this one's.
    private void Follow()
    {
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (!Equals(_shownFor, key))
        {
            _shownFor = key;
            _hasBuild = false;
            _countedAt = DateTimeOffset.MinValue;
            BuildText = "loading...";
            LiveCount = 0;
        }
    }

    private void ShowBuild(AdminBuildInfo build)
    {
        _hasBuild = true;
        BuildText = !build.Available ? "dev build" : build.BuildNumber > 0 ? $"{build.BuildNumber} · {build.Display}" : build.Display;
    }

    private async Task ReadBuildAsync(Uri server, object? key, CancellationToken cancellationToken)
    {
        try
        {
            var build = await _admin.GetBuildAsync(server, cancellationToken);
            if (Equals(_shownFor, key))
            {
                ShowBuild(build);
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the server's build for the admin sidebar");
            if (Equals(_shownFor, key))
            {
                BuildText = "load failed";
            }
        }
    }

    private async Task ReadLiveAsync(Uri server, object? key, CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await _admin.ListSessionsAsync(server, cancellationToken);
            if (Equals(_shownFor, key))
            {
                LiveCount = sessions.Items.Count;
                _countedAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            // The last count stays.
            _logger.LogWarning(ex, "Could not read the live sessions for the admin sidebar");
        }
    }
}
