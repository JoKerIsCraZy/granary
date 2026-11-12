using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>Where one part of the library page stands.</summary>
public enum LibraryLoadState
{
    /// <summary>Nothing asked yet.</summary>
    Idle,
    Loading,
    Ready,

    /// <summary>Loaded, with nothing to show.</summary>
    Empty,

    /// <summary>Unknown or hidden library.</summary>
    Unavailable,
    Error,
}

/// <summary>
/// The words the library page shows for a failed load; never the exception's own text. Where the web words the same
/// failure, they are its words (<c>components/loadErrorDescription.ts</c>): the server not answering, or busy, is its
/// timeout text, which tells the viewer the problem is the server and not the page.
/// </summary>
public static class LibraryErrors
{
    public static string Unreachable => AppText.Get("web.common.ui.loadError.timeout");

    public static string Busy => AppText.Get("web.common.ui.loadError.timeout");

    public static string Denied => AppText.Get("granary.loadError.denied");

    public static string Gone => AppText.Get("granary.loadError.gone");

    public static string Generic => AppText.Get("web.common.ui.loadError.generic");

    /// <summary>
    /// The failures a load catches instead of letting them escape: transport errors, problem answers, malformed
    /// answers and cancellations (a newer load's, or a timeout of the HTTP client). The caller drops whatever a
    /// cancelled load reports, so it never overwrites the state of the load that replaced it.
    /// </summary>
    public static bool IsExpected(Exception exception) =>
        exception is HttpRequestException or SiloApiException or JsonException or OperationCanceledException;

    public static string Describe(Exception exception) => exception switch
    {
        SiloApiException { Status: 401 or 403 } => Denied,
        SiloApiException { Status: 404 } => Gone,
        SiloApiException { Status: 429 or 503 } => Busy,
        SiloApiException or JsonException => Generic,
        _ => Unreachable,
    };
}
