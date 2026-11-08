namespace Granary.Core.Api.Catalog;

/// <summary>Local theme songs (docs/api-contract.md 7.19; silo-server docs/catalog-api.md, "Local theme songs, V2").</summary>
public sealed class ThemesApi(SiloHttp http)
{
    /// <summary>Whether the server serves themes to the acting profile.</summary>
    public Task<ThemeSongsCapability> GetCapabilityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/catalog/themes/capabilities", SiloAuth.Profile, ThemesJsonContext.Default.ThemeSongsCapability,
            cancellationToken);

    /// <summary>
    /// A grant for theme <paramref name="themeId"/> of <paramref name="ownerId"/>: the original when it is one of
    /// <paramref name="accepted"/>, else an AAC conversion where the server can make one (406 when it cannot).
    /// </summary>
    public Task<ThemePlayback> CreatePlaybackAsync(Uri server, string ownerId, string themeId, IReadOnlyList<ThemeAudioFormat> accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        string path = "api/v2/catalog/items/" + SiloHttp.PathSegment(ownerId, nameof(ownerId)) + "/themes/"
            + SiloHttp.PathSegment(themeId, nameof(themeId)) + "/playback";
        // Without formats the server sends the original, as for a client that does not say what it decodes.
        return accepted.Count == 0
            ? http.SendAsync(HttpMethod.Post, server, path, SiloAuth.Profile, ThemesJsonContext.Default.ThemePlayback, cancellationToken)
            : http.PostAsync(server, path, SiloAuth.Profile, new ThemePlaybackRequest(accepted), ThemesJsonContext.Default.ThemePlaybackRequest,
                ThemesJsonContext.Default.ThemePlayback, cancellationToken);
    }
}
