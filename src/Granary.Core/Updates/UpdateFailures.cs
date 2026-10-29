using System.Globalization;

namespace Granary.Core.Updates;

/// <summary>What the log may say about a failed update step.</summary>
internal static class UpdateFailures
{
    /// <summary>
    /// The kind of failure, never its message: an HTTP or Velopack message can carry an address, and after a redirect an
    /// address can carry a token.
    /// </summary>
    public static string KindOf(Exception ex) => ex is HttpRequestException { StatusCode: { } status }
        ? "HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture)
        : ex.GetType().Name;
}
