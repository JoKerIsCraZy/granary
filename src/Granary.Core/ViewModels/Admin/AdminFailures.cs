using Granary.Core.Api;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The words for an administrator's action that failed. The web shows the server's own sentence; Granary branches on the
/// status and names the failure in the language on screen: refused, gone, busy or unreachable as a page load says them,
/// and anything else in the action's own words (<paramref name="fallback"/>, the web's text for it).
/// </summary>
public static class AdminFailures
{
    public static string Describe(Exception exception, string fallback) => exception switch
    {
        SiloApiException { Status: 401 or 403 or 404 or 429 or 503 } => LibraryErrors.Describe(exception),
        SiloApiException => fallback,
        _ => LibraryErrors.Describe(exception),
    };

    /// <summary>412: someone changed it after it was read.</summary>
    public static bool IsChanged(Exception exception) => exception is SiloApiException { Status: 412 };
}
