using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Core.Api.Playback;

/// <summary>
/// The read right before playback (docs/api-contract.md, 7.8). It acts for the selected profile: progress
/// and track preferences are the profile's, and the device header (sent by the identity handler) picks the
/// device-level preference layer.
/// </summary>
public sealed class WatchApi(SiloHttp http)
{
    /// <summary>A movie or episode with its files, resume point and preferences; a series is 422 <c>validation_failed</c>.</summary>
    /// <param name="fileId">The file the client means to play, which also fills that file's on-demand markers.</param>
    /// <param name="images">From <see cref="ImageSizes.For"/>; leave it out for the server's default sizes.</param>
    /// <exception cref="ArgumentException"><paramref name="contentId"/> is not an id.</exception>
    public Task<WatchDetail> GetWatchAsync(Uri server, string contentId, string? fileId = null, ImageParameters? images = null,
        CancellationToken cancellationToken = default)
    {
        string path = "api/v2/watch/" + SiloHttp.PathSegment(contentId, nameof(contentId));
        string query = new QueryBuilder().Add("file_id", fileId).Add(images).ToString();
        return http.GetAsync(server, path + query, SiloAuth.Profile, PlaybackJsonContext.Default.WatchDetail, cancellationToken);
    }

    /// <summary>
    /// The seek-bar previews of <paramref name="fileId"/> (docs/api-contract.md, 11.4), for a file whose
    /// <see cref="WatchFileVersion.TrickplayAvailable"/> is true; 404 <c>not_found</c> when it has none.
    /// </summary>
    public Task<TrickplayManifest> GetTrickplayAsync(Uri server, string contentId, string fileId, CancellationToken cancellationToken = default)
    {
        string path = "api/v2/watch/" + SiloHttp.PathSegment(contentId, nameof(contentId)) + "/trickplay";
        string query = new QueryBuilder().Add("file_id", fileId).ToString();
        return http.GetAsync(server, path + query, SiloAuth.Profile, PlaybackJsonContext.Default.TrickplayManifest, cancellationToken);
    }
}

/// <summary>A file's seek-bar previews as the server lists them; sheet addresses carry their own authorization.</summary>
public sealed record TrickplayManifest
{
    public string FileId { get; init => field = value ?? ""; } = "";

    /// <summary>Media time each thumbnail covers.</summary>
    public int IntervalMs { get; init; }

    public int ThumbnailWidth { get; init; }

    public int ThumbnailHeight { get; init; }

    public int TileColumns { get; init; }

    public int TileRows { get; init; }

    public int ThumbnailCount { get; init; }

    public IReadOnlyList<TrickplaySheet> Sheets { get; init => field = value ?? []; } = [];

    /// <summary>When the sheet addresses stop working; read the previews again for fresh ones.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record TrickplaySheet
{
    public int Index { get; init; }

    /// <summary>A signed root-relative artwork path, or an absolute presigned address.</summary>
    public string Url { get; init => field = value ?? ""; } = "";
}
