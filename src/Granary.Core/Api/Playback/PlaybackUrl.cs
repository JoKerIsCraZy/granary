using Granary.Core.Api.Images;

namespace Granary.Core.Api.Playback;

/// <summary>
/// Makes a plan's media, subtitle and font URLs absolute (docs/api-contract.md, 9.2). These requests carry the
/// bearer token, so only URLs on the saved server are accepted.
/// </summary>
public static class PlaybackUrl
{
    /// <summary>
    /// Appends a root-relative URL to <paramref name="server"/> as a string, keeping a reverse-proxy path prefix and the
    /// query exactly as sent (use <see cref="Uri.OriginalString"/>), or keeps an absolute one beneath the server. Answers
    /// null for anything else: without <c>authorized_media_origins_v1</c> no plan names another origin, and one that
    /// did must not receive the client's credentials.
    /// </summary>
    public static Uri? Resolve(Uri server, string? value)
    {
        var resolved = ArtworkUrl.Resolve(server, value);
        return resolved is not null && SiloRequestOptions.IsBeneath(resolved, server) ? resolved : null;
    }
}
