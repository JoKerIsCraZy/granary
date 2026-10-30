namespace Granary.Core.Trailers;

/// <summary>Why a trailer can't play in Granary; the viewer is offered YouTube's own page instead.</summary>
public enum TrailerFailure
{
    None,

    /// <summary>The owner allows the video only on YouTube (the player's errors 101 and 150).</summary>
    EmbedRefused,

    /// <summary>Removed or private (100).</summary>
    NotFound,

    /// <summary>YouTube refused the embed's set-up (153): no Referer or origin reached it.</summary>
    Configuration,

    /// <summary>The player could not take the request (2), play it (5), or failed in a way it did not name.</summary>
    PlayerError,

    /// <summary>The player did not answer in time.</summary>
    TimedOut,

    /// <summary>Windows' WebView2 runtime is missing or could not start.</summary>
    NoRuntime,

    /// <summary>A WebView2 process failed.</summary>
    BrowserFailed,
}
