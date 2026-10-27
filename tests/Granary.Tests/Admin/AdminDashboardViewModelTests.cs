using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Dashboard: the web's default widgets, each with its own failure, read again once a minute.</summary>
public sealed class AdminDashboardViewModelTests
{
    private readonly BrowseFakeSilo _silo = AdminPayloads.Dashboard();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly TestClock _clock = new(AdminPayloads.Now);

    private AdminSidebarViewModel? _sidebar;

    private AdminSidebarViewModel Sidebar => _sidebar ??= new(_session, new AdminApi(_silo.Http), _clock, _silo.Logs.For<AdminSidebarViewModel>());

    private AdminDashboardViewModel Create() =>
        new(_session, new AdminApi(_silo.Http), Sidebar, _navigation, _clock, _silo.Logs.For<AdminDashboardViewModel>());

    [Fact]
    public async Task Activate_ShowsTheTiles()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Ready, dashboard.State);
        Assert.Equal(new AdminStatTile("Active Streams", "2", "2 sessions"), dashboard.ActiveStreamsTile);
        Assert.Equal(new AdminStatTile("Egress now", "12 Mbps", "node + server egress"), dashboard.EgressNowTile);
        Assert.Equal(new AdminStatTile("Profiles · 24h", "3", "watched on this server"), dashboard.ProfilesTile);
        Assert.Equal(new AdminStatTile("Total Movies", "1,990", "2,048 files"), dashboard.MoviesTile);
        Assert.Equal(new AdminStatTile("Total Shows", "310", "10,432 files"), dashboard.ShowsTile);
        Assert.Equal(new AdminStatTile("Storage", "101.8 TB", "12,480 files"), dashboard.StorageTile);
        Assert.Equal("Updated less than 1 minute ago", dashboard.UpdatedText);
    }

    [Fact]
    public async Task Activate_AsksForTheWebsWindows()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal("24", Assert.Single(_silo.To("api/v2/admin/stats/timeseries")).Query("hours"));
        Assert.Equal("24", Assert.Single(_silo.To("api/v2/admin/stats/playback-activity")).Query("hours"));
        Assert.Equal("7", Assert.Single(_silo.To("api/v2/admin/stats/top-activity")).Query("days"));
        Assert.Equal("200", Assert.Single(_silo.To("api/v2/admin/users")).Query("limit"));
        Assert.All(_silo.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task HealthStrip_ComposesStatusBuildAndNodes()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(
        [
            new AdminHealthCell("Version", "1715 · 13ad7a74"),
            new AdminHealthCell("Uptime", "3d 4h", "restart required", AdminTone.Warn),
            new AdminHealthCell("Postgres", "OK", "0.06 ms"),
            new AdminHealthCell("Redis", "Not configured", "optional"),
            // The switched-off node counts on neither side.
            new AdminHealthCell("Nodes", "1/2", "healthy", AdminTone.Warn),
            new AdminHealthCell("Errors · 24h", "3", "1,204 warnings", AdminTone.Error),
        ], dashboard.Health);
    }

    [Fact]
    public async Task NowPlaying_ShowsEachStreamAsTheWebsCard()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Ready, dashboard.SessionsState);
        Assert.Equal("View all 2 streams ›", dashboard.StreamsLinkText);
        Assert.Equal("", dashboard.MoreStreamsText);
        var episode = dashboard.Streams[0];
        Assert.Equal("Past and Present", episode.Title);
        Assert.Equal("S1 · E3 — Dark", episode.Subtitle);
        Assert.Equal("Direct Stream", episode.MethodLabel);
        Assert.Equal("Granary 0.3.0", episode.Client);
        Assert.Equal("Elena", episode.Profile);
        Assert.Equal("elena", episode.Username);
        Assert.Equal("E", episode.Initial);
        Assert.Equal("5m ago", episode.Elapsed);
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/dark.webp?sig=x"), episode.PosterUrl);
        var movie = dashboard.Streams[1];
        Assert.Equal(("Dune", "Movie", "Direct Play", "Silo for Android", "p2", true, false),
            (movie.Title, movie.Subtitle, movie.MethodLabel, movie.Client, movie.Profile, movie.IsPaused, movie.CanOpen));

        int activityRequests = 0;
        dashboard.ActivityRequested += (_, _) => activityRequests++;
        episode.OpenCommand.Execute(episode.ContentId);
        dashboard.OpenActivityCommand.Execute(null);

        Assert.Equal((AppRoute.Item, (object?)"episode:dark-s01e03"), Assert.Single(_navigation.Navigations));
        // The admin area switches its own page: no other place in the history.
        Assert.Equal(1, activityRequests);
    }

    [Fact]
    public async Task Read_GoesToTheAdminSidebarToo()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();
        await Sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1715 · 13ad7a74", Sidebar.BuildText);
        Assert.Equal("2 live", Sidebar.LiveText);
        // The sidebar had all it needed.
        Assert.Single(_silo.To("api/v2/admin/system/build"));
        Assert.Single(_silo.To("api/v2/admin/sessions"));
    }

    [Fact]
    public async Task RecentActivity_ListsWhoStartedWhat()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(
        [
            new AdminActivityRow("elena", "Elena", "Past and Present", "5m ago · Granary 0.3.0"),
            new AdminActivityRow("kim", "p2", "Dune", "2h ago · Silo for Android"),
        ], dashboard.Recent);
    }

    [Fact]
    public async Task NothingPlaying_IsEmptyNotFailed()
    {
        _silo.Map("api/v2/admin/sessions", AdminPayloads.NoSessions);
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Empty, dashboard.SessionsState);
        Assert.Equal("View activity ›", dashboard.StreamsLinkText);
        Assert.Empty(dashboard.Streams);
        Assert.Equal(new AdminStatTile("Active Streams", "0", "0 sessions"), dashboard.ActiveStreamsTile);
    }

    [Fact]
    public async Task Charts_ShowTheLast24Hours()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Ready, dashboard.PlaybackState);
        Assert.Equal(25, dashboard.PlaybackColumns.Count);
        Assert.Equal("3 sessions", dashboard.PlaybackTotalText);
        Assert.Equal(1, dashboard.PlaybackColumns[^1].DirectStream);
        Assert.Equal(LibraryLoadState.Ready, dashboard.EgressState);
        Assert.Equal("Total 48.3 GB", dashboard.EgressTotalText);
        Assert.Equal("Peak 133 Mbps", dashboard.EgressPeakText);
        Assert.Equal(1441, dashboard.Egress.Values.Count);
    }

    [Fact]
    public async Task Leaderboards_BarAgainstTheBusiestRow()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(
        [
            new AdminBarRow("1", "Dark", "4 plays", "3.1h", 1, "series:dark", dashboard.OpenTitleCommand),
            new AdminBarRow("2", "movie:dune", "1 play", "1m", 0.25, "movie:dune", dashboard.OpenTitleCommand),
        ], dashboard.TopTitles);
        Assert.Equal(
        [
            new AdminBarRow("1", "Elena · elena", "4 plays", "3.1h", 1),
            new AdminBarRow("2", "Kids · elena", "2 plays", "50m", 0.5),
        ], dashboard.TopProfiles);
    }

    [Fact]
    public async Task LibrariesAndUsers_ReadAsTheWebsLists()
    {
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(
        [
            new AdminLibraryRow("Filme", "movies · 1 path", true),
            new AdminLibraryRow("Serien", "series · 2 paths", false),
        ], dashboard.Libraries);
        // Most recently active first; only administrators carry a role.
        Assert.Equal(
        [
            new AdminUserRow("elena", "elena@example.test", "owner", "Just now", true),
            new AdminUserRow("kim", "kim@example.test", "", "1h ago", false),
            new AdminUserRow("zoe", "", "", "—", true),
        ], dashboard.Users);
    }

    [Fact]
    public async Task FailedSource_FailsOnlyItsOwnParts()
    {
        _silo.Map("api/v2/admin/stats", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        _silo.Map("api/v2/admin/server/status", _ => throw new HttpRequestException("down"));
        _silo.Map("api/v2/admin/stats/top-activity", _ => BrowseFakeSilo.Json("{ not json"));
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Ready, dashboard.State);
        Assert.True(dashboard.MoviesTile.IsUnavailable);
        Assert.True(dashboard.StorageTile.IsUnavailable);
        Assert.False(dashboard.ActiveStreamsTile.IsUnavailable);
        Assert.Equal(LibraryLoadState.Error, dashboard.HealthState);
        Assert.Empty(dashboard.Health);
        Assert.Equal(LibraryLoadState.Error, dashboard.TopTitlesState);
        Assert.Equal(LibraryLoadState.Error, dashboard.TopProfilesState);
        Assert.Equal(LibraryLoadState.Ready, dashboard.UsersState);
        Assert.Equal(3, _silo.Logs.Lines.Count(line => line.StartsWith("Warning", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NodesUnknown_SaysSo_AndAMissingSampleWaits()
    {
        _silo.Map("api/v2/admin/nodes", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        _clock.Now = AdminPayloads.Now.AddMinutes(5);
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(new AdminHealthCell("Nodes", "—", "unavailable"), dashboard.Health[4]);
        Assert.Equal(new AdminStatTile("Egress now", "—", "waiting for a sample"), dashboard.EgressNowTile);
    }

    [Fact]
    public async Task NotAnAdministrator_ShowsWhy()
    {
        _silo.Map("api/v2/admin/stats", _ => BrowseFakeSilo.Problem(403, "forbidden"));
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Error, dashboard.State);
        Assert.True(dashboard.HasError);
        Assert.Contains("administrators", dashboard.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tick_ReadsAgainOnceAMinutePassed()
    {
        var dashboard = Create();
        await dashboard.ActivateAsync();

        _clock.Now = AdminPayloads.Now.AddSeconds(59);
        await dashboard.TickAsync();
        Assert.Single(_silo.To("api/v2/admin/stats"));

        _clock.Now = AdminPayloads.Now.AddSeconds(61);
        _silo.Map("api/v2/admin/sessions", AdminPayloads.NoSessions);
        await dashboard.TickAsync();

        Assert.Equal(2, _silo.To("api/v2/admin/stats").Count);
        Assert.Equal(LibraryLoadState.Empty, dashboard.SessionsState);
        Assert.Equal("Updated less than 1 minute ago", dashboard.UpdatedText);
    }

    [Fact]
    public async Task Tick_MovesTheUpdatedText_BeforeTheReadEnds()
    {
        var dashboard = Create();
        await dashboard.ActivateAsync();
        _silo.Map("api/v2/admin/stats", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return BrowseFakeSilo.Json(AdminPayloads.Stats);
        });

        _clock.Now = AdminPayloads.Now.AddSeconds(90);
        var tick = dashboard.TickAsync();

        Assert.Equal("Updated 1 minute ago", dashboard.UpdatedText);
        // An automatic read shows no "Refreshing...".
        Assert.False(dashboard.IsRefreshing);
        dashboard.Deactivate();
        await tick;
        Assert.Equal(LibraryLoadState.Ready, dashboard.State);
    }

    [Fact]
    public async Task Activate_KeepsAFreshDashboard_AndReadsAStaleOne()
    {
        var dashboard = Create();
        await dashboard.ActivateAsync();

        _clock.Now = AdminPayloads.Now.AddSeconds(30);
        await dashboard.ActivateAsync();
        Assert.Single(_silo.To("api/v2/admin/stats"));

        _clock.Now = AdminPayloads.Now.AddMinutes(2);
        await dashboard.ActivateAsync();
        Assert.Equal(2, _silo.To("api/v2/admin/stats").Count);
    }

    [Fact]
    public async Task Refresh_ReadsAtOnce_AndSaysSoMeanwhile()
    {
        var dashboard = Create();
        await dashboard.ActivateAsync();
        var release = new TaskCompletionSource();
        _silo.Map("api/v2/admin/stats", async (_, _) =>
        {
            await release.Task;
            return BrowseFakeSilo.Json(AdminPayloads.Stats);
        });

        var refresh = dashboard.RefreshCommand.ExecuteAsync(null);

        Assert.True(dashboard.IsRefreshing);
        Assert.Equal("Refreshing...", dashboard.RefreshText);
        Assert.False(dashboard.RefreshCommand.CanExecute(null));
        release.SetResult();
        await refresh;
        Assert.False(dashboard.IsRefreshing);
        Assert.Equal("Refresh", dashboard.RefreshText);
        Assert.Equal(2, _silo.To("api/v2/admin/stats").Count);
    }

    [Fact]
    public async Task Deactivate_DropsARunningRead()
    {
        var dashboard = Create();
        _silo.Map("api/v2/admin/stats", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return BrowseFakeSilo.Json(AdminPayloads.Stats);
        });

        var load = dashboard.ActivateAsync();
        dashboard.Deactivate();
        await load;

        Assert.Equal(LibraryLoadState.Loading, dashboard.State);
        Assert.DoesNotContain(_silo.Logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherProfile_StartsOver()
    {
        var dashboard = Create();
        await dashboard.ActivateAsync();

        _session.ProfileId = "8";
        var stats = new TaskCompletionSource();
        _silo.Map("api/v2/admin/stats", async (_, _) =>
        {
            await stats.Task;
            return BrowseFakeSilo.Json(AdminPayloads.Stats);
        });
        var load = dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Loading, dashboard.State);
        Assert.Equal("", dashboard.UpdatedText);
        stats.SetResult();
        await load;
        Assert.Equal(LibraryLoadState.Ready, dashboard.State);
    }

    [Fact]
    public async Task WithoutASession_ShowsNothing()
    {
        _session.State = SessionState.SignedOut;
        var dashboard = Create();

        await dashboard.ActivateAsync();

        Assert.Equal(LibraryLoadState.Idle, dashboard.State);
        Assert.Empty(_silo.Requests);
    }
}
