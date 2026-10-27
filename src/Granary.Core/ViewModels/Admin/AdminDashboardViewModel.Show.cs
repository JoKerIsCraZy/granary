using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Images;

namespace Granary.Core.ViewModels.Admin;

// What the dashboard shows of a read, part by part, in the web's words.
public sealed partial class AdminDashboardViewModel
{
    private const int StreamsShown = 4;
    private const int TopRows = 8;
    private const int UsersShown = 8;
    private const int RecentShown = 10;

    [ObservableProperty]
    public partial AdminStatTile ActiveStreamsTile { get; private set; } = Waiting("Active Streams");

    [ObservableProperty]
    public partial AdminStatTile EgressNowTile { get; private set; } = Waiting("Egress now");

    [ObservableProperty]
    public partial AdminStatTile ProfilesTile { get; private set; } = Waiting("Profiles · 24h");

    [ObservableProperty]
    public partial AdminStatTile MoviesTile { get; private set; } = Waiting("Total Movies");

    [ObservableProperty]
    public partial AdminStatTile ShowsTile { get; private set; } = Waiting("Total Shows");

    [ObservableProperty]
    public partial AdminStatTile StorageTile { get; private set; } = Waiting("Storage");

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
    public partial string StreamsLinkText { get; private set; } = "View activity ›";

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
        PlaybackTotalText = AdminText.Sessions(started);
        Egress = AdminCharts.Rate(read.Egress.Value, point => point.EgressKbps / 1000d);
        EgressState = StateOf(read.Egress, !Egress.HasSamples);
        EgressTotalText = read.Egress.Value is { } egress ? $"Total {AdminText.DataTotal(egress.EgressBytes)}" : "";
        EgressPeakText = $"Peak {AdminText.Mbps(Egress.Peak)}";
        ShowTop(read.Top);
        Libraries = [.. (read.Libraries.Value?.Items ?? []).Select(library => new AdminLibraryRow(library.Name,
            $"{library.Type} · {library.Paths.Count} {(library.Paths.Count == 1 ? "path" : "paths")}", library.Enabled))];
        LibrariesState = StateOf(read.Libraries, Libraries.Count == 0);
        Users = [.. (read.Users.Value?.Items ?? []).OrderByDescending(user => user.LastActiveAt ?? DateTimeOffset.MinValue)
            .ThenBy(user => user.Username, StringComparer.Ordinal).Take(UsersShown).Select(user => UserRow(user, now))];
        UsersState = StateOf(read.Users, Users.Count == 0);
    }

    private void ShowTiles(DashboardRead read, DateTimeOffset now)
    {
        ActiveStreamsTile = read.Sessions.Value is { } sessions
            ? new("Active Streams", AdminText.Count(sessions.Items.Count), AdminText.Sessions(sessions.Items.Count))
            : Unavailable("Active Streams");
        var latest = AdminCharts.LatestFresh(read.Egress.Value, now);
        EgressNowTile = read.Egress.Value is null ? Unavailable("Egress now")
            : latest is null ? new("Egress now", AdminText.None, "waiting for a sample")
            : new("Egress now", AdminText.Mbps(latest.EgressKbps / 1000d), "node + server egress");
        ProfilesTile = read.Playback.Value is { } playback
            ? new("Profiles · 24h", AdminText.Count(playback.ProfilesActive24h), "watched on this server")
            : Unavailable("Profiles · 24h");
        if (read.Stats.Value is { } stats)
        {
            MoviesTile = new("Total Movies", AdminText.Count(stats.TotalMovies), AdminText.Files(stats.TotalMovieFiles));
            ShowsTile = new("Total Shows", AdminText.Count(stats.TotalShows), AdminText.Files(stats.TotalShowFiles));
            StorageTile = new("Storage", AdminText.Storage(stats.TotalStorageBytes), AdminText.Files(stats.TotalFiles));
        }
        else
        {
            MoviesTile = Unavailable("Total Movies");
            ShowsTile = Unavailable("Total Shows");
            StorageTile = Unavailable("Storage");
        }
    }

    // Version, uptime and node health come from their own endpoints, as the web composes the strip.
    private static List<AdminHealthCell> HealthOf(AdminServerStatus status, AdminBuildInfo? build, AdminNodeCollection? nodes, DateTimeOffset now)
    {
        string display = build?.Display is { Length: > 0 } shown ? shown : "unknown";
        var health = status.Health;
        // A disabled node is not expected to be healthy, so it counts on neither side.
        var enabled = nodes?.Items.Where(node => node.Enabled).ToList();
        int healthy = enabled?.Count(node => node.Healthy) ?? 0;
        return
        [
            new("Version", build is { BuildNumber: > 0 } ? $"{build.BuildNumber} · {display}" : display, build?.Dirty == true ? "dirty build" : ""),
            new("Uptime", AdminText.Uptime(status.StartedAt, now), status.RestartRequired ? "restart required" : "",
                status.RestartRequired ? AdminTone.Warn : AdminTone.Normal),
            DependencyCell("Postgres", health?.Postgres),
            DependencyCell("Redis", health?.Redis),
            enabled is null ? new("Nodes", AdminText.None, "unavailable")
                : enabled.Count == 0 ? new("Nodes", "none", "this server transcodes")
                : new("Nodes", $"{healthy}/{enabled.Count}", "healthy", healthy < enabled.Count ? AdminTone.Warn : AdminTone.Normal),
            health is null ? new("Errors · 24h", AdminText.None)
                : new("Errors · 24h", AdminText.Count(health.Errors24h), $"{AdminText.Count(health.Warnings24h)} warnings",
                    health.Errors24h > 0 ? AdminTone.Error : AdminTone.Normal),
        ];
    }

    // Configured before up: a server without Redis is healthy, not an outage.
    private static AdminHealthCell DependencyCell(string label, AdminServerHealthComponent? component) => component switch
    {
        null => new(label, AdminText.None),
        { Configured: false } => new(label, "Not configured", "optional"),
        { Ok: not true } => new(label, "Unreachable", "", AdminTone.Error),
        _ => new(label, "OK", component.LatencyMs is { } latency ? AdminText.Latency(latency) : ""),
    };

    private void ShowSessions(AdminPlaybackSessionCollection? sessions, Uri server, DateTimeOffset now)
    {
        var items = sessions?.Items ?? [];
        SessionsState = sessions is null ? LibraryLoadState.Error : items.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        StreamsLinkText = items.Count > 0 ? $"View all {items.Count} streams ›" : "View activity ›";
        MoreStreamsText = items.Count > StreamsShown ? $"+{items.Count - StreamsShown} more active streams" : "";
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
            string.Join(" · ", new[] { AdminText.Ago(session.StartedAt, now), AdminText.Client(session) }.Where(part => part.Length > 0))))];
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
            AdminText.Plays(profile.Plays),
            AdminText.WatchTime(profile.TotalSeconds),
            profile.Plays / (double)profilePlays))];
        TopTitlesState = StateOf(top, TopTitles.Count == 0);
        TopProfilesState = StateOf(top, TopProfiles.Count == 0);
    }

    // "Kids · elena", or the account alone when the profile is named as it is.
    private static string ProfileLabel(AdminDashboardTopProfile profile)
    {
        string account = profile.Username is { Length: > 0 } username ? username : $"User #{profile.UserId}";
        string name = profile.ProfileName is { Length: > 0 } profileName ? profileName
            : profile.ProfileId is { Length: > 0 } id ? id : account;
        return name == account ? account : $"{name} · {account}";
    }

    private static AdminUserRow UserRow(AdminUser user, DateTimeOffset now) => new(
        user.Username,
        user.Email,
        user.Role != "admin" ? "" : user.IsOwner ? "owner" : user.Role,
        user.LastActiveAt is { } at ? AdminText.Ago(at, now) : AdminText.None,
        user.Enabled);
}
