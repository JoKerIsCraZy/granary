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

    /// <summary>Adding a title outside the library to the watchlist also requests it, for this profile.</summary>
    public bool WatchlistRequests { get; init; }

    /// <summary>Titles outside the library can go on the watchlist (<c>/api/v2/watchlist/titles</c>).</summary>
    public bool WatchlistTitlesSupported { get; init; }
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

    /// <summary>The account that made the request; only its owner can cancel it.</summary>
    public string? RequestedByUserId { get; init; }

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

/// <summary>A Discover row (Trending, Popular, …): a page of its titles (the first, on the hub).</summary>
public sealed record DiscoverSection
{
    public string Key { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public int Page { get; init; }

    /// <summary>
    /// The page to ask for next when the server read several TMDB pages to fill this one (for a rating-restricted
    /// profile); page + 1 would repeat titles then.
    /// </summary>
    public int? NextPage { get; init; }

    public int TotalPages { get; init; }

    public IReadOnlyList<RequestMediaResult> Results { get; init => field = value ?? []; } = [];
}

/// <summary><c>GET /api/v2/requests/discover/{studios|networks|genres}</c>.</summary>
public sealed record DiscoverBrandCollection
{
    public IReadOnlyList<DiscoverBrand> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A studio, network or genre to browse: a logo, or a name on a gradient for a genre.</summary>
public sealed record DiscoverBrand
{
    public string Slug { get; init => field = value ?? ""; } = "";

    public string DisplayName { get; init => field = value ?? ""; } = "";

    public string? LogoUrl { get; init; }

    /// <summary>"#475569", a genre tile's colours.</summary>
    public string? GradientFrom { get; init; }

    public string? GradientTo { get; init; }

    /// <summary>A genre that has series as well as movies.</summary>
    public bool SeriesSupported { get; init; }
}

/// <summary><c>GET /api/v2/requests/discover/browse/{kind}/{slug}</c>: a page of a studio's, network's or genre's titles.</summary>
public sealed record DiscoverBrowsePage
{
    public string DisplayName { get; init => field = value ?? ""; } = "";

    public string? LogoUrl { get; init; }

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

    /// <summary>The request's id, when the viewer may see it (their account's own, or an admin).</summary>
    public string? RequestId { get; init; }

    /// <summary>The viewer asked to hear when someone else's request lands.</summary>
    public bool Following { get; init; }

    public bool RequestedByViewer { get; init; }

    public RequestDownload? Download { get; init; }
}

/// <summary><c>GET /api/v2/requests/detail/{media_type}/{tmdb_id}</c>: a title from TMDB in full (what the title page uses).</summary>
public sealed record RequestMediaDetail
{
    public long TmdbId { get; init; }

    /// <summary><c>movie</c> or <c>series</c>.</summary>
    public string MediaType { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public string? Tagline { get; init; }

    public string? PosterPath { get; init; }

    public string? BackdropPath { get; init; }

    public string? ContentRating { get; init; }

    /// <summary>Minutes, for a movie.</summary>
    public int? Runtime { get; init; }

    public int? NumberOfSeasons { get; init; }

    public int? NumberOfEpisodes { get; init; }

    /// <summary>A series' status on TMDB ("Returning Series", "Ended").</summary>
    public string? Status { get; init; }

    /// <summary>YYYY-MM-DD.</summary>
    public string? FirstAirDate { get; init; }

    public string? LastAirDate { get; init; }

    public double? VoteAverage { get; init; }

    public int? VoteCount { get; init; }

    public string? ImdbId { get; init; }

    public string? Director { get; init; }

    public IReadOnlyList<string> Creators { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Genres { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Networks { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> ProductionCompanies { get; init => field = value ?? []; } = [];

    public IReadOnlyList<RequestMediaCastMember> Cast { get; init => field = value ?? []; } = [];

    public IReadOnlyList<RequestMediaSeason> Seasons { get; init => field = value ?? []; } = [];

    public IReadOnlyList<RequestMediaResult> Recommendations { get; init => field = value ?? []; } = [];

    /// <summary><c>available</c> when the library has it.</summary>
    public string Availability { get; init => field = value ?? ""; } = "";

    public string? LibraryContentId { get; init; }

    public bool InWatchlist { get; init; }

    public RequestMediaState Request { get; init => field = value ?? new(); } = new();
}

public sealed record RequestMediaCastMember
{
    public string Name { get; init => field = value ?? ""; } = "";

    public string? Character { get; init; }

    public int Order { get; init; }

    public string? ProfilePath { get; init; }
}

/// <summary>A series' season as TMDB lists it, with what the library has and what was asked for.</summary>
public sealed record RequestMediaSeason
{
    public int SeasonNumber { get; init; }

    public string? Name { get; init; }

    /// <summary>YYYY-MM-DD; absent before TMDB dates it.</summary>
    public string? AirDate { get; init; }

    public int EpisodeCount { get; init; }

    /// <summary><c>missing</c>, <c>partial</c> or <c>available</c>.</summary>
    public string Availability { get; init => field = value ?? ""; } = "";

    public bool Requested { get; init; }

    public string? PosterPath { get; init; }
}

/// <summary>The body of <c>POST /api/v2/requests</c>. Without <see cref="Seasons"/> the server picks a series' seasons itself.</summary>
public sealed record MediaRequestCreate
{
    public string MediaType { get; init => field = value ?? ""; } = "";

    public long TmdbId { get; init; }

    public string Title { get; init => field = value ?? ""; } = "";

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public string? PosterPath { get; init; }

    public string? BackdropPath { get; init; }

    public IReadOnlyList<int>? Seasons { get; init; }
}

/// <summary><c>PUT /api/v2/watchlist/titles/{media_type}/{tmdb_id}</c>: the title as it now stands.</summary>
public sealed record WatchlistTitleEntry
{
    /// <summary>The library title it went onto the watchlist as, when the library has it.</summary>
    public string? ItemId { get; init; }

    public RequestMediaState Request { get; init => field = value ?? new(); } = new();
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

    /// <summary>
    /// A studio's or network's logo as the server names it: a TMDB image at any size, possibly through one of TMDB's
    /// filters (<c>https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/abc.png</c>). Null for any other URL.
    /// </summary>
    public static Uri? Logo(string? url) =>
        url is { Length: <= 512 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host
            && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && LogoPath().IsMatch(uri.AbsolutePath)
            ? uri
            : null;

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{1,128}\.(jpg|jpeg|png|webp|svg)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePath();

    // No SVG: a bitmap cannot draw one, and a tile without its logo shows the name instead.
    [GeneratedRegex(@"^/t/p/(w[0-9]{2,4}|original)(_filter\([a-z]{1,16}(,[0-9a-fA-F]{6}){0,4}\))?/[A-Za-z0-9_-]{1,128}\.(jpg|jpeg|png|webp)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LogoPath();

    [GeneratedRegex(@"^(w[0-9]{2,4}|original)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeName();
}

/// <summary>What a browse page lists: a studio's movies, a network's series, a genre's movies or series.</summary>
public enum DiscoverBrowseKind
{
    Studio,
    Network,
    Genre,
}
