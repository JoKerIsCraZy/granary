using Granary.Core.Api;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The words for an administrator's action that failed. The web shows the server's own sentence; Granary branches on the
/// status and names the failure in the language on screen: refused, gone, busy or unreachable as a page load says them,
/// and anything else in the action's own words (<paramref name="fallback"/>, the web's text for it).
/// </summary>
public static class AdminFailures
{
    /// <summary>
    /// The failures an admin page catches: a load's (<see cref="LibraryErrors.IsExpected"/>), and a call refused unsent
    /// because another account or profile acts on the server now (<see cref="ProfileChangedException"/>).
    /// </summary>
    public static bool IsExpected(Exception exception) => LibraryErrors.IsExpected(exception) || exception is ProfileChangedException;

    /// <summary>
    /// A call refused unsent after a switch is worded as a refusal: the page belongs to the account and profile it was
    /// opened as, and the one acting now may not use it.
    /// </summary>
    public static string Describe(Exception exception, string fallback) => exception switch
    {
        ProfileChangedException => LibraryErrors.Denied,
        SiloApiException { Status: 401 or 403 or 404 or 429 or 503 } => LibraryErrors.Describe(exception),
        SiloApiException => fallback,
        _ => LibraryErrors.Describe(exception),
    };

    /// <summary>As <see cref="LibraryErrors.Describe"/>, for a read: a switch is a refusal.</summary>
    public static string DescribeLoad(Exception exception) =>
        exception is ProfileChangedException ? LibraryErrors.Denied : LibraryErrors.Describe(exception);

    /// <summary>412: someone changed it after it was read.</summary>
    public static bool IsChanged(Exception exception) => exception is SiloApiException { Status: 412 };
}
