using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Images;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

// What the dashboard shows of a read, part by part, in the web's words.
public sealed partial class AdminDashboardViewModel
{
    private const int StreamsShown = 4;
    private const int TopRows = 8;
    private const int UsersShown = 8;
    private const int RecentShown = 10;

    [ObservableProperty]
    public partial AdminStatTile ActiveStreamsTile { get; private set; } = Waiting(ActiveStreamsLabel);

    [ObservableProperty]
    public partial AdminStatTile EgressNowTile { get; private set; } = Waiting(EgressNowLabel);

    [ObservableProperty]
    public partial AdminStatTile ProfilesTile { get; private set; } = Waiting(ProfilesLabel);

    [ObservableProperty]
    public partial AdminStatTile MoviesTile { get; private set; } = Waiting(MoviesLabel);

    [ObservableProperty]
    public partial AdminStatTile ShowsTile { get; private set; } = Waiting(ShowsLabel);

    [ObservableProperty]
    public partial AdminStatTile StorageTile { get; private set; } = Waiting(StorageLabel);

    /// <summary>Version, Uptime, Postgres, Redis, Nodes and Errors · 24h; empty when the server's status failed.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AdminHealthCell> Health { get; private set; } = [];

    [ObservableProperty]
    public partial LibraryLoadState HealthState { get; private set; }

    /// <summary>The first streams of Now Playing.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AdminStreamCard> Streams { get; private set; } = [];

    /// <summary>Now Playing and Recent Activity: Empty when nothing plays.</summary>
    [ObservableProperty]
    public partial LibraryLoadState SessionsState { get; private set; }

    /// <summary>"View all 6 streams ›", or "View activity ›" when nothing plays.</summary>
    [ObservableProperty]
    public partial string StreamsLinkText { get; private set; } = AppText.Get("web.admin.dashboard.widgets.nowPlaying.viewActivity");

    /// <summary>"+2 more active streams" past the cards shown; empty otherwise.</summary>
    [ObservableProperty]
    public partial string MoreStreamsText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AdminActivityRow> Recent { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AdminPlaybackColumn> PlaybackColumns { get; private set; } = [];

    [ObservableProperty]
    public partial LibraryLoadState PlaybackState { get; private set; }

    /// <summary>"12 sessions" started in the chart's window.</summary>
    [ObservableProperty]
    public partial string PlaybackTotalText { get; private set; } = "";

    [ObservableProperty]
    public partial AdminRateSeries Egress { get; private set; } = AdminRateSeries.Empty;

    [ObservableProperty]
    public partial LibraryLoadState EgressState { get; private set; }

    /// <summary>"Total 48.3 GB"; empty when the server did not say.</summary>
    [ObservableProperty]
    public partial string EgressTotalText { get; private set; } = "";

    /// <summary>"Peak 133 Mbps".</summary>
    [ObservableProperty]
    public partial string EgressPeakText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AdminBarRow> TopTitles { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AdminBarRow> TopProfiles { get; private set; } = [];

    [ObservableProperty]
    public partial LibraryLoadState TopTitlesState { get; private set; }

    [ObservableProperty]
    public partial LibraryLoadState TopProfilesState { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<AdminLibraryRow> Libraries { get; private set; } = [];

    [ObservableProperty]
    public partial LibraryLoadState LibrariesState { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<AdminUserRow> Users { get; private set; } = [];

    [ObservableProperty]
    public partial LibraryLoadState UsersState { get; private set; }

    private static string ActiveStreamsLabel => AppText.Get("web.admin.dashboard.widgets.activeStreams.label");

    private static string EgressNowLabel => AppText.Get("web.admin.dashboard.widgets.egressNow.label");

    private static string ProfilesLabel => AppText.Get("web.admin.dashboard.widgets.profilesActive.label");

    private static string MoviesLabel => AppText.Get("web.admin.dashboard.widgets.movies.label");

    private static string ShowsLabel => AppText.Get("web.admin.dashboard.widgets.shows.label");

    private static string StorageLabel => AppText.Get("web.admin.dashboard.widgets.storage.label");

    private static AdminStatTile Waiting(string label) => new(label, AdminText.None, "");

    private static AdminStatTile Unavailable(string label) => new(label, AdminText.None, "", IsUnavailable: true);

    private static LibraryLoadState StateOf<T>(Fetched<T> fetched, bool isEmpty)
        where T : class =>
        fetched.Value is null ? LibraryLoadState.Error : isEmpty ? LibraryLoadState.Empty : LibraryLoadState.Ready;

    private void Show(DashboardRead read, Uri server, DateTimeOffset now)
    {
        ShowTiles(read, now);
        HealthState = read.Status.Value is null ? LibraryLoadState.Error : LibraryLoadState.Ready;
        Health = read.Status.Value is { } status ? HealthOf(status, read.Build.Value, read.Nodes.Value, now) : [];
        ShowSessions(read.Sessions.Value, server, now);
        PlaybackColumns = AdminCharts.PlaybackColumns(read.Playback.Value, ChartHours, read.Playback.Value?.To ?? now);
        int started = PlaybackColumns.Sum(column => column.Total);
        PlaybackState = StateOf(read.Playback, started == 0);
        PlaybackTotalText = AdminText.StartedSessions(started);
        Egress = AdminCharts.Rate(read.Egress.Value, point => point.EgressKbps / 1000d);
        EgressState = StateOf(read.Egress, !Egress.HasSamples);
        EgressTotalText = read.Egress.Value is { } egress
            ? AppText.Get("web.admin.dashboard.widgets.egress.total", ("value", AdminText.DataTotal(egress.EgressBytes)))
            : "";
        EgressPeakText = AppText.Get("web.admin.dashboard.widgets.egress.peak", ("value", AdminText.Mbps(Egress.Peak)));
        ShowTop(read.Top);
        Libraries = [.. (read.Libraries.Value?.Items ?? []).Select(library =>
            new AdminLibraryRow(library.Name, AdminText.LibraryDetail(library.Type, library.Paths.Count), library.Enabled))];
        LibrariesState = StateOf(read.Libraries, Libraries.Count == 0);
        Users = [.. (read.Users.Value?.Items ?? []).OrderByDescending(user => user.LastActiveAt ?? DateTimeOffset.MinValue)
            .ThenBy(user => user.Username, StringComparer.Ordinal).Take(UsersShown).Select(user => UserRow(user, now))];
        UsersState = StateOf(read.Users, Users.Count == 0);
    }

    private void ShowTiles(DashboardRead read, DateTimeOffset now)
    {
        ActiveStreamsTile = read.Sessions.Value is { } sessions
            ? new(ActiveStreamsLabel, AdminText.Count(sessions.Items.Count), AdminText.Sessions(sessions.Items.Count))
            : Unavailable(ActiveStreamsLabel);
        var latest = AdminCharts.LatestFresh(read.Egress.Value, now);
        EgressNowTile = read.Egress.Value is null ? Unavailable(EgressNowLabel)
            : latest is null ? new(EgressNowLabel, AdminText.None, AppText.Get("web.admin.dashboard.widgets.egressNow.waiting"))
            : new(EgressNowLabel, AdminText.Mbps(latest.EgressKbps / 1000d), AppText.Get("web.admin.dashboard.widgets.egressNow.source"));
        ProfilesTile = read.Playback.Value is { } playback
            ? new(ProfilesLabel, AdminText.Count(playback.ProfilesActive24h), AppText.Get("web.admin.dashboard.widgets.profilesActive.detail"))
            : Unavailable(ProfilesLabel);
        if (read.Stats.Value is { } stats)
        {
            MoviesTile = new(MoviesLabel, AdminText.Count(stats.TotalMovies), AdminText.Files(stats.TotalMovieFiles));
            ShowsTile = new(ShowsLabel, AdminText.Count(stats.TotalShows), AdminText.Files(stats.TotalShowFiles));
            StorageTile = new(StorageLabel, AdminText.Storage(stats.TotalStorageBytes), AdminText.Files(stats.TotalFiles));
        }
        else
        {
            MoviesTile = Unavailable(MoviesLabel);
            ShowsTile = Unavailable(ShowsLabel);
            StorageTile = Unavailable(StorageLabel);
        }
    }

    // Version, uptime and node health come from their own endpoints, as the web composes the strip.
    private static List<AdminHealthCell> HealthOf(AdminServerStatus status, AdminBuildInfo? build, AdminNodeCollection? nodes, DateTimeOffset now)
    {
        string display = build?.Display is { Length: > 0 } shown ? shown : HealthWord("versionUnknown");
        var health = status.Health;
        // A disabled node is not expected to be healthy, so it counts on neither side.
        var enabled = nodes?.Items.Where(node => node.Enabled).ToList();
        int healthy = enabled?.Count(node => node.Healthy) ?? 0;
        string nodesLabel = HealthWord("nodes");
        string errorsLabel = HealthWord("errors24h");
        return
        [
            new(AdminHealthKind.Version, HealthWord("version"), build is { BuildNumber: > 0 } ? $"{build.BuildNumber} · {display}" : display,
                build?.Dirty == true ? HealthWord("dirtyBuild") : ""),
            new(AdminHealthKind.Uptime, HealthWord("uptime"), AdminText.Uptime(status.StartedAt, now), status.RestartRequired ? HealthWord("restartRequired") : "",
                status.RestartRequired ? AdminTone.Warn : AdminTone.Normal),
            // The web names the two by their product names in every language.
            DependencyCell(AdminHealthKind.Postgres, "Postgres", health?.Postgres),
            DependencyCell(AdminHealthKind.Redis, "Redis", health?.Redis),
            enabled is null ? new(AdminHealthKind.Nodes, nodesLabel, AdminText.None, HealthWord("nodesUnavailable"))
                : enabled.Count == 0 ? new(AdminHealthKind.Nodes, nodesLabel, HealthWord("nodesNone"), HealthWord("nodesLocal"))
                : new(AdminHealthKind.Nodes, nodesLabel, $"{healthy}/{enabled.Count}", HealthWord("nodesHealthy"),
                    healthy < enabled.Count ? AdminTone.Warn : AdminTone.Normal),
            health is null ? new(AdminHealthKind.Errors, errorsLabel, AdminText.None)
                : new(AdminHealthKind.Errors, errorsLabel, AdminText.Count(health.Errors24h),
                    AppText.Get("web.admin.dashboard.widgets.healthStrip.warnings", ("count", health.Warnings24h), ("count_text", AppText.Number(health.Warnings24h))),
                    health.Errors24h > 0 ? AdminTone.Error : AdminTone.Normal),
        ];
    }

    // Configured before up: a server without Redis is healthy, not an outage.
    private static AdminHealthCell DependencyCell(AdminHealthKind kind, string label, AdminServerHealthComponent? component) => component switch
    {
        null => new(kind, label, AdminText.None),
        { Configured: false } => new(kind, label, HealthWord("notConfigured"), HealthWord("optional")),
        { Ok: not true } => new(kind, label, HealthWord("unreachable"), "", AdminTone.Error),
        _ => new(kind, label, HealthWord("ok"), component.LatencyMs is { } latency ? AdminText.Latency(latency) : ""),
    };

    // A word of the web's health strip (components/admin/dashboard/widgets/HealthStripWidget.tsx).
    private static string HealthWord(string name) => name switch
    {
        "version" => AppText.Get("web.admin.dashboard.widgets.healthStrip.version"),
        "versionUnknown" => AppText.Get("web.admin.dashboard.widgets.healthStrip.versionUnknown"),
        "dirtyBuild" => AppText.Get("web.admin.dashboard.widgets.healthStrip.dirtyBuild"),
        "uptime" => AppText.Get("web.admin.dashboard.widgets.healthStrip.uptime"),
        "restartRequired" => AppText.Get("web.admin.dashboard.widgets.healthStrip.restartRequired"),
        "nodes" => AppText.Get("web.admin.dashboard.widgets.healthStrip.nodes"),
        "nodesUnavailable" => AppText.Get("web.admin.dashboard.widgets.healthStrip.nodesUnavailable"),
        "nodesNone" => AppText.Get("web.admin.dashboard.widgets.healthStrip.nodesNone"),
        "nodesLocal" => AppText.Get("web.admin.dashboard.widgets.healthStrip.nodesLocal"),
        "nodesHealthy" => AppText.Get("web.admin.dashboard.widgets.healthStrip.nodesHealthy"),
        "errors24h" => AppText.Get("web.admin.dashboard.widgets.healthStrip.errors24h"),
        "notConfigured" => AppText.Get("web.admin.dashboard.widgets.healthStrip.notConfigured"),
        "optional" => AppText.Get("web.admin.dashboard.widgets.healthStrip.optional"),
        "unreachable" => AppText.Get("web.admin.dashboard.widgets.healthStrip.unreachable"),
        _ => AppText.Get("web.admin.dashboard.widgets.healthStrip.ok"),
    };

    private void ShowSessions(AdminPlaybackSessionCollection? sessions, Uri server, DateTimeOffset now)
    {
        var items = sessions?.Items ?? [];
        SessionsState = sessions is null ? LibraryLoadState.Error : items.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        StreamsLinkText = items.Count > 0
            ? AppText.Get("web.admin.dashboard.widgets.nowPlaying.viewAll", ("count", items.Count), ("count_text", items.Count))
            : AppText.Get("web.admin.dashboard.widgets.nowPlaying.viewActivity");
        int more = items.Count - StreamsShown;
        MoreStreamsText = more > 0 ? AppText.Get("web.admin.dashboard.widgets.nowPlaying.moreStreams", ("count", more), ("count_text", more)) : "";
        Streams = [.. items.Take(StreamsShown).Select(session => new AdminStreamCard(
            AdminText.Title(session),
            AdminText.Subtitle(session),
            AdminPlayMethod.Of(session),
            AdminText.Client(session),
            AdminText.Profile(session),
            AdminText.Username(session),
            AdminText.Ago(session.StartedAt, now),
            session.IsPaused,
            ArtworkUrl.Resolve(server, session.PosterUrl),
            string.IsNullOrEmpty(session.ContentId) ? null : session.ContentId,
            OpenTitleCommand))];
        Recent = [.. items.Take(RecentShown).Select(session => new AdminActivityRow(
            AdminText.Username(session),
            AdminText.Profile(session),
            AdminText.Title(session),
            string.Join(" · ", new[]
            {
                AdminText.Ago(session.StartedAt, now, AppText.Get("web.admin.dashboard.widgets.recentActivity.justNow")), AdminText.Client(session),
            }.Where(part => part.Length > 0))))];
    }

    private void ShowTop(Fetched<AdminDashboardTopActivity> top)
    {
        var titles = top.Value?.Titles.Take(TopRows).ToList() ?? [];
        int titlePlays = Math.Max(1, titles.Select(title => title.Plays).DefaultIfEmpty().Max());
        TopTitles = [.. titles.Select((title, i) => new AdminBarRow(
            AdminText.Count(i + 1),
            title.Title.Length > 0 ? title.Title : title.MediaItemId,
            AdminText.Plays(title.Plays),
            AdminText.WatchTime(title.TotalSeconds),
            title.Plays / (double)titlePlays,
            title.MediaItemId.Length > 0 ? title.MediaItemId : null,
            OpenTitleCommand))];
        var profiles = top.Value?.Profiles.Take(TopRows).ToList() ?? [];
        int profilePlays = Math.Max(1, profiles.Select(profile => profile.Plays).DefaultIfEmpty().Max());
        TopProfiles = [.. profiles.Select((profile, i) => new AdminBarRow(
            AdminText.Count(i + 1),
            ProfileLabel(profile),
            AdminText.ProfilePlays(profile.Plays),
            AdminText.WatchTime(profile.TotalSeconds),
            profile.Plays / (double)profilePlays))];
        TopTitlesState = StateOf(top, TopTitles.Count == 0);
        TopProfilesState = StateOf(top, TopProfiles.Count == 0);
    }

    // "Kids · elena", or the account alone when the profile is named as it is.
    private static string ProfileLabel(AdminDashboardTopProfile profile)
    {
        string account = profile.Username is { Length: > 0 } username
            ? username
            : AppText.Get("web.admin.dashboard.widgets.topProfiles.userFallback", ("id", profile.UserId));
        string name = profile.ProfileName is { Length: > 0 } profileName ? profileName
            : profile.ProfileId is { Length: > 0 } id ? id : account;
        return name == account ? account : $"{name} · {account}";
    }

    private static AdminUserRow UserRow(AdminUser user, DateTimeOffset now) => new(
        user.Username,
        user.Email,
        user.Role != "admin" ? "" : AdminText.Role(user.Role, user.IsOwner),
        user.LastActiveAt is { } at ? AdminText.Ago(at, now, AppText.Get("web.admin.dashboard.widgets.users.justNow")) : AdminText.None,
        user.Enabled);
}
