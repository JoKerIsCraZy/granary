namespace Granary.Core.Api.Admin;

/// <summary>
/// The administrator's view of the server, as the web's Admin → Dashboard and Activity read it, and the commands Activity
/// sends a playback session (docs/api-contract.md, 7.16). Every call needs an administrator account; anyone else gets 403.
/// </summary>
public sealed class AdminApi(SiloHttp http)
{
    public Task<AdminDashboardStats> GetStatsAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/stats", SiloAuth.Profile, AdminJsonContext.Default.AdminDashboardStats, cancellationToken);

    public Task<AdminServerStatus> GetServerStatusAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/server/status", SiloAuth.Profile, AdminJsonContext.Default.AdminServerStatus, cancellationToken);

    public Task<AdminBuildInfo> GetBuildAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/system/build", SiloAuth.Profile, AdminJsonContext.Default.AdminBuildInfo, cancellationToken);

    public Task<AdminNodeCollection> ListNodesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/nodes", SiloAuth.Profile, AdminJsonContext.Default.AdminNodeCollection, cancellationToken);

    private const int SessionsPageSize = 100;
    private const int MaxSessionPages = 100;

    /// <summary>
    /// The playback sessions running now, every page of them as the web reads them: a session a later page names again
    /// counts once, and a cursor that repeats fails the read.
    /// </summary>
    public async Task<AdminPlaybackSessionCollection> ListSessionsAsync(Uri server, CancellationToken cancellationToken = default)
    {
        var sessions = new Dictionary<string, AdminPlaybackSession>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < MaxSessionPages; page++)
        {
            string path = $"api/v2/admin/sessions?limit={SessionsPageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var collection = await http.GetAsync(server, path, SiloAuth.Profile, AdminJsonContext.Default.AdminPlaybackSessionCollection,
                cancellationToken);
            foreach (var session in collection.Items)
            {
                sessions[session.SessionId] = session;
            }
            if (collection.Page is not { HasMore: true } more)
            {
                return new AdminPlaybackSessionCollection { Items = [.. sessions.Values] };
            }
            if (string.IsNullOrEmpty(more.NextCursor) || !cursors.Add(more.NextCursor))
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server's session pages do not continue.");
            }
            cursor = more.NextCursor;
        }
        throw new HttpRequestException(HttpRequestError.InvalidResponse, "Too many session pages.");
    }

    public Task<AdminDashboardTimeseries> GetTimeseriesAsync(Uri server, int hours, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/admin/stats/timeseries?hours={hours}", SiloAuth.Profile, AdminJsonContext.Default.AdminDashboardTimeseries,
            cancellationToken);

    public Task<AdminDashboardPlaybackActivity> GetPlaybackActivityAsync(Uri server, int hours, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/admin/stats/playback-activity?hours={hours}", SiloAuth.Profile,
            AdminJsonContext.Default.AdminDashboardPlaybackActivity, cancellationToken);

    public Task<AdminDashboardTopActivity> GetTopActivityAsync(Uri server, int days, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/admin/stats/top-activity?days={days}", SiloAuth.Profile,
            AdminJsonContext.Default.AdminDashboardTopActivity, cancellationToken);

    /// <summary>The first <paramref name="limit"/> accounts (at most 200 a page).</summary>
    public Task<AdminUserCollection> ListUsersAsync(Uri server, int limit, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/admin/users?limit={limit}", SiloAuth.Profile, AdminJsonContext.Default.AdminUserCollection, cancellationToken);

    /// <summary>Every library with its folders and scan state (the administrator's view of <c>GET /api/v2/libraries</c>).</summary>
    public Task<AdminLibraryCollection> ListLibrariesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/libraries", SiloAuth.Profile, AdminJsonContext.Default.AdminLibraryCollection, cancellationToken);

    /// <summary>The accounts seen at <paramref name="ip"/> over the last <paramref name="days"/>, a page at a time.</summary>
    public Task<AdminIpUserCollection> ListIpUsersAsync(Uri server, string ip, int days, int limit, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        string query = $"ip={Uri.EscapeDataString(ip)}&days={days}&limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        return http.GetAsync(server, $"api/v2/admin/ips?{query}", SiloAuth.Profile, AdminJsonContext.Default.AdminIpUserCollection, cancellationToken);
    }

    /// <summary>
    /// Pause, resume, stop or message (<see cref="AdminSessionCommands"/>): sent to the player on its realtime lane. A stop
    /// without a lane ends the session on the server instead (<c>fallback_scheduled</c>).
    /// </summary>
    public Task<AdminPlaybackCommandReceipt> SendCommandAsync(Uri server, string sessionId, string action, AdminPlaybackCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!AdminSessionCommands.All.Contains(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Not a playback command.");
        }
        return http.PostAsync(server, $"api/v2/admin/sessions/{Uri.EscapeDataString(sessionId)}/{action}", SiloAuth.Profile, command,
            AdminJsonContext.Default.AdminPlaybackCommand, AdminJsonContext.Default.AdminPlaybackCommandReceipt, cancellationToken);
    }

    /// <summary>Ends the session for good: its playback authority is revoked first, the player is told as best it can be.</summary>
    public Task<AdminPlaybackTerminateReceipt> TerminateAsync(Uri server, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return http.PostAsync(server, $"api/v2/admin/sessions/{Uri.EscapeDataString(sessionId)}/terminate", SiloAuth.Profile,
            new AdminPlaybackTerminate(), AdminJsonContext.Default.AdminPlaybackTerminate, AdminJsonContext.Default.AdminPlaybackTerminateReceipt,
            cancellationToken);
    }
}

/// <summary>The sequenced playback commands of <see cref="AdminApi.SendCommandAsync"/>.</summary>
public static class AdminSessionCommands
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string Message = "message";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pause, Resume, Stop, Message };
}
