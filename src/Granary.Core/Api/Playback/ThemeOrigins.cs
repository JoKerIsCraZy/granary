namespace Granary.Core.Api.Playback;

/// <summary>Where a theme grant's audio may come from (docs/api-contract.md 7.19, <see cref="PlaybackUrl.ResolveTheme"/>).</summary>
public enum ThemeOrigins
{
    /// <summary>The server's own origin only: the themes capability says <c>local_direct_play</c>, or nothing is known.</summary>
    Server,

    /// <summary>
    /// Also a proxy node's: the capability says <c>routed</c>, on a server that does not authorize media origins, so a node is
    /// checked conservatively.
    /// </summary>
    Routed,

    /// <summary>
    /// Also a proxy node's the server vouches for: <c>routed</c>, and its playback capabilities offer
    /// <c>authorized_media_origins_v1</c>, as video takes a node's stream.
    /// </summary>
    Authorized,
}
