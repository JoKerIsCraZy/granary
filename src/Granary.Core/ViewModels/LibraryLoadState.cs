using System.Text.Json;
using Granary.Core.Api;

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

/// <summary>The words the library page shows for a failed load; never the exception's own text.</summary>
public static class LibraryErrors
{
    public const string Unreachable = "Couldn't reach the server. Check the connection and try again.";
    public const string Busy = "The server is busy right now. Try again in a moment.";
    public const string Denied = "This profile isn't allowed to see this.";
    public const string Gone = "This isn't available any more.";
    public const string Generic = "Something went wrong while loading. Try again.";

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
