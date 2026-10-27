using System.Text.Json.Serialization;

namespace Granary.Core.Api.Admin;

/// <summary><c>GET /api/v2/admin/stats</c>: the server's totals.</summary>
public sealed record AdminDashboardStats
{
    public int ActiveStreams { get; init; }

    public int TotalMovies { get; init; }

    public int TotalMovieFiles { get; init; }

    public int TotalShows { get; init; }

    public int TotalShowFiles { get; init; }

    public int TotalFiles { get; init; }

    public long TotalStorageBytes { get; init; }

    public int TotalUsers { get; init; }
}

/// <summary><c>GET /api/v2/admin/server/status</c>: when the server started and how its database and cache answer.</summary>
public sealed record AdminServerStatus
{
    public DateTimeOffset StartedAt { get; init; }

    public AdminServerHealth? Health { get; init; }

    public bool RestartRequired { get; init; }
}

public sealed record AdminServerHealth
{
    [JsonPropertyName("errors_24h")]
    public int Errors24h { get; init; }

    [JsonPropertyName("warnings_24h")]
    public int Warnings24h { get; init; }

    public AdminServerHealthComponent? Postgres { get; init; }

    public AdminServerHealthComponent? Redis { get; init; }
}

/// <param name="Ok">Null when the server did not check it.</param>
public sealed record AdminServerHealthComponent(bool Configured, bool? Ok, double? LatencyMs);

/// <summary><c>GET /api/v2/admin/system/build</c>.</summary>
/// <param name="Display">The server's own words for its build, such as its revision.</param>
/// <param name="Dirty">Built from a tree with uncommitted changes.</param>
public sealed record AdminBuildInfo(bool Available, string Display, int BuildNumber, string Revision, bool Dirty);

/// <summary>A stream node of <c>GET /api/v2/admin/nodes</c>.</summary>
public sealed record AdminNode(string Id, string Name, string Type, bool Enabled, bool Healthy, int ActiveJobs, int EgressKbps);

public sealed record AdminNodeCollection
{
    public IReadOnlyList<AdminNode> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A playback session of <c>GET /api/v2/admin/sessions</c>, as the dashboard and Activity show it.</summary>
public sealed record AdminPlaybackSession
{
    public string SessionId { get; init => field = value ?? ""; } = "";

    public string UserId { get; init => field = value ?? ""; } = "";

    public string Username { get; init => field = value ?? ""; } = "";

    public string ProfileId { get; init => field = value ?? ""; } = "";

    public string? ProfileName { get; init; }

    public string MediaFileId { get; init => field = value ?? ""; } = "";

    public string MediaTitle { get; init => field = value ?? ""; } = "";

    public string MediaType { get; init => field = value ?? ""; } = "";

    public string? ContentId { get; init; }

    public string? SeriesName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? EpisodeName { get; init; }

    public string? PosterUrl { get; init; }

    public string PlayMethod { get; init => field = value ?? ""; } = "";

    /// <summary>direct, remux, direct_stream or transcode; null when the server does not know.</summary>
    public string? EffectivePlayMethod { get; init; }

    public bool IsPaused { get; init; }

    public double PositionSeconds { get; init; }

    public int? FileDuration { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? ClientName { get; init; }

    public string? ClientLabel { get; init; }

    public string? ClientVersion { get; init; }

    /// <summary>The client with its build and channel, when that says more than <see cref="ClientLabel"/>.</summary>
    public string? ClientLabelFull { get; init; }

    public string? ClientIp { get; init; }

    public string? NodeDisplayName { get; init; }

    public string ReportingNode { get; init => field = value ?? ""; } = "";

    /// <summary>local or remote.</summary>
    public string StreamLocation { get; init => field = value ?? ""; } = "";

    public string? SourceContainer { get; init; }

    public string? SourceVideoCodec { get; init; }

    public string? SourceVideoResolution { get; init; }

    /// <summary>The source's pixel height, which tells a full-resolution transcode from a downscale.</summary>
    public int? SourceVideoHeight { get; init; }

    public string? SourceAudioCodec { get; init; }

    public int? SourceAudioChannels { get; init; }

    public string? SourceAudioLanguage { get; init; }

    /// <summary>The source audio track's own title, as the file tags it.</summary>
    public string? SourceAudioTitle { get; init; }

    public int? SourceBitrateKbps { get; init; }

    /// <summary>fmp4, mpegts, or the source container for direct play.</summary>
    public string? OutputContainer { get; init; }

    /// <summary>hls or http.</summary>
    public string? OutputProtocol { get; init; }

    public string? TargetVideoCodec { get; init; }

    public string? TargetResolution { get; init; }

    public string? TargetAudioCodec { get; init; }

    public int? TargetAudioChannels { get; init; }

    public int? StreamBitrateKbps { get; init; }

    public string? VideoDecision { get; init; }

    public string? AudioDecision { get; init; }

    /// <summary>The audio is converted; read only by servers without <see cref="EffectivePlayMethod"/>.</summary>
    public bool TranscodeAudio { get; init; }

    public string? TranscodeHwAccel { get; init; }

    /// <summary>hardware or software, while HDR is tone mapped.</summary>
    public string? ToneMapMode { get; init; }

    /// <summary>Set once the server routed the session; older servers name only <see cref="NodeDisplayName"/>.</summary>
    public string? RoutingWorkload { get; init; }

    /// <summary>Where the work runs: transcode, proxy or api.</summary>
    public string? RoutingExecution { get; init; }

    public string? RoutingExecutionNodeName { get; init; }

    /// <summary>Where the stream leaves for the viewer: proxy or api.</summary>
    public string? RoutingEgress { get; init; }

    public string? RoutingEgressNodeName { get; init; }

    public bool HasPlaybackControl { get; init; }
}

public sealed record AdminPlaybackSessionCollection
{
    public IReadOnlyList<AdminPlaybackSession> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary><c>GET /api/v2/admin/stats/timeseries</c>: sampled streams and egress.</summary>
public sealed record AdminDashboardTimeseries
{
    public long EgressBytes { get; init; }

    public int ResolutionSeconds { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset To { get; init; }

    public IReadOnlyList<AdminDashboardTimeseriesPoint> Points { get; init => field = value ?? []; } = [];
}

public sealed record AdminDashboardTimeseriesPoint(DateTimeOffset T, int Streams, int Direct, int Remux, int Transcode, int EgressKbps, int DownloadEgressKbps);

/// <summary><c>GET /api/v2/admin/stats/playback-activity</c>: playback starts by hour and method.</summary>
public sealed record AdminDashboardPlaybackActivity
{
    public int Hours { get; init; }

    public int BucketSeconds { get; init; }

    [JsonPropertyName("profiles_active_24h")]
    public int ProfilesActive24h { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset To { get; init; }

    public IReadOnlyList<AdminDashboardPlaybackBucket> Buckets { get; init => field = value ?? []; } = [];
}

/// <param name="Hour">The bucket's start.</param>
public sealed record AdminDashboardPlaybackBucket(DateTimeOffset Hour, int Direct, int Remux, int Transcode);

/// <summary><c>GET /api/v2/admin/stats/top-activity</c>: the most played titles and the most active profiles.</summary>
public sealed record AdminDashboardTopActivity
{
    public int Days { get; init; }

    public IReadOnlyList<AdminTopTitle> Titles { get; init => field = value ?? []; } = [];

    public IReadOnlyList<AdminDashboardTopProfile> Profiles { get; init => field = value ?? []; } = [];
}

public sealed record AdminTopTitle(string MediaItemId, string MediaType, string Title, int Plays, long TotalSeconds);

public sealed record AdminDashboardTopProfile(string ProfileId, string ProfileName, string UserId, string Username, int Plays, long TotalSeconds);

/// <summary>An account of <c>GET /api/v2/admin/users</c>.</summary>
public sealed record AdminUser
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Username { get; init => field = value ?? ""; } = "";

    public string Email { get; init => field = value ?? ""; } = "";

    /// <summary>admin or user.</summary>
    public string Role { get; init => field = value ?? ""; } = "";

    public bool Enabled { get; init; }

    public bool IsOwner { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }
}

public sealed record AdminUserCollection
{
    public IReadOnlyList<AdminUser> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A library of <c>GET /api/v2/libraries</c>, as an administrator sees it: its folders and scan state.</summary>
public sealed record AdminLibrary
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    public string Type { get; init => field = value ?? ""; } = "";

    public bool Enabled { get; init; }

    public int SortOrder { get; init; }

    public IReadOnlyList<string> Paths { get; init => field = value ?? []; } = [];

    public DateTimeOffset? LastScannedAt { get; init; }

    public string? ScanWarningCode { get; init; }

    public string? ScanWarningMessage { get; init; }
}

public sealed record AdminLibraryCollection
{
    public IReadOnlyList<AdminLibrary> Items { get; init => field = value ?? []; } = [];
}
