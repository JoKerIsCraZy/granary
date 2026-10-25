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
}
