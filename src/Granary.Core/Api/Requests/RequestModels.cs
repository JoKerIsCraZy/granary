using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Requests;

/// <summary><c>GET /api/v2/requests/status</c> (only what the client uses).</summary>
public sealed record RequestFeatureStatus
{
    /// <summary>The Requests entry and pages show only while this holds.</summary>
    public bool RequestsEnabled { get; init; }

    public bool DownloadProgressSupported { get; init; }

    public bool FollowSupported { get; init; }

    public bool SeasonRequestsSupported { get; init; }

    public bool WatchlistRequests { get; init; }
}

/// <summary><c>GET /api/v2/requests/mine</c>: a page of the account's requests, newest first.</summary>
public sealed record MediaRequestPage
{
    public IReadOnlyList<MediaRequest> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }
}

/// <summary>A request for a title from TMDB (only the members the request pages use are modelled).</summary>
public sealed record MediaRequest
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary><c>movie</c> or <c>series</c>.</summary>
    public string MediaType { get; init => field = value ?? ""; } = "";

    public long TmdbId { get; init; }

    public string Title { get; init => field = value ?? ""; } = "";

    public int? Year { get; init; }

    /// <summary>A TMDB image path ("/abc.jpg"), see <see cref="TmdbImages"/>.</summary>
    public string? PosterPath { get; init; }

    /// <summary><c>pending</c>, <c>approved</c>, <c>queued</c>, <c>downloading</c>, <c>completed</c>.</summary>
    public string Status { get; init => field = value ?? ""; } = "";

    /// <summary><c>active</c>, <c>declined</c>, <c>cancelled</c>, <c>failed</c>.</summary>
    public string Outcome { get; init => field = value ?? ""; } = "";

    /// <summary>The state the request pages show, derived by the server; see <see cref="RequestStates"/>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    public string? OutcomeReason { get; init; }

    public string? LastError { get; init; }

    /// <summary>The library title once it arrived.</summary>
    public string? LibraryContentId { get; init; }

    public IReadOnlyList<int> Seasons { get; init => field = value ?? []; } = [];

    public IReadOnlyList<RequestSeasonProgress> SeasonProgress { get; init => field = value ?? []; } = [];

    public RequestDownload? Download { get; init; }

    /// <summary>Where the request was sent; empty until it was. Only the count matters here.</summary>
    public IReadOnlyList<JsonElement> Targets { get; init => field = value ?? []; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RequestSeasonProgress(int SeasonNumber, int EpisodesAired, int EpisodesAvailable);

/// <summary>How far a request's downloads are.</summary>
public sealed record RequestDownload
{
    /// <summary><c>queued</c>, <c>downloading</c>, <c>paused</c>, <c>stalled</c>, <c>importing</c>, <c>import_blocked</c>; open set.</summary>
    public string Phase { get; init => field = value ?? ""; } = "";

    public double? Percent { get; init; }

    public DateTimeOffset? EstimatedCompletionAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The body of <c>POST /api/v2/requests/{id}/cancel</c>.</summary>
public sealed record RequestCancel(string? Reason = null);

/// <summary><c>GET /api/v2/requests/discover</c>: the Discover rows.</summary>
public sealed record DiscoverSectionCollection
{
    public IReadOnlyList<DiscoverSection> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A Discover row (Trending, Popular, …): its first page of titles.</summary>
public sealed record DiscoverSection
{
    public string Key { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public int Page { get; init; }

    public int TotalPages { get; init; }

    public IReadOnlyList<RequestMediaResult> Results { get; init => field = value ?? []; } = [];
}

/// <summary>A title from TMDB, as search and Discover list it.</summary>
public sealed record RequestMediaResult
{
    public long TmdbId { get; init; }

    /// <summary><c>movie</c> or <c>series</c>.</summary>
    public string MediaType { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public int? Year { get; init; }

    public string? PosterPath { get; init; }

    /// <summary><c>available</c> when the library has it; see <see cref="LibraryContentId"/>.</summary>
    public string Availability { get; init => field = value ?? ""; } = "";

    public string? LibraryContentId { get; init; }

    public bool InWatchlist { get; init; }

    public RequestMediaState Request { get; init => field = value ?? new(); } = new();
}

/// <summary>Whether the viewer can request a title, and the request it already has.</summary>
public sealed record RequestMediaState
{
    public bool Requestable { get; init; }

    /// <summary>Why not: <c>already_requested</c>, <c>already_available</c>, <c>requests_disabled</c>, <c>blocked</c>, <c>quota_exceeded</c>.</summary>
    public string? Reason { get; init; }

    public string? Status { get; init; }

    public string? State { get; init; }
}

/// <summary>Values of <see cref="MediaRequest.State"/>.</summary>
public static class RequestStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Processing = "processing";
    public const string PartiallyAvailable = "partially_available";
    public const string Available = "available";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>TMDB's image server, which the web loads request posters from.</summary>
public static partial class TmdbImages
{
    private const string Host = "image.tmdb.org";

    /// <summary>
    /// The poster at a TMDB size (<c>w154</c>, <c>w342</c>); null without a path, or for one that is not a TMDB image
    /// file name ("/abc123.jpg"): a path from the server never leaves TMDB's image folder.
    /// </summary>
    public static Uri? Poster(string? path, string size = "w342") =>
        path is not null && ImagePath().IsMatch(path) && SizeName().IsMatch(size)
            && Uri.TryCreate($"https://{Host}/t/p/{size}{path}", UriKind.Absolute, out var uri) && uri.Host == Host
            ? uri
            : null;

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{1,128}\.(jpg|jpeg|png|webp|svg)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePath();

    [GeneratedRegex(@"^(w[0-9]{2,4}|original)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeName();
}
