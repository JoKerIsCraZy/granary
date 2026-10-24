using Granary.Core.Api;

namespace Granary.Core.ViewModels;

/// <summary>User-facing wording for failed catalog calls on Home, "Explore all" and Search. Never the exception's text.</summary>
internal static class BrowseFailureText
{
    public const string Busy = "The server is busy. Try again in a moment.";

    /// <param name="fallback">What to say for an error answer without a more specific wording.</param>
    /// <param name="notFound">What to say for a 404, for example a row that was removed.</param>
    public static string Describe(Exception ex, string fallback, string? notFound = null) => ex switch
    {
        SiloApiException { Status: 404 } when notFound is not null => notFound,
        SiloApiException { Status: 429 or 503 } => Busy,
        SiloApiException => fallback,
        _ => ApiFailureText.ForTransport(ex),
    };
}
