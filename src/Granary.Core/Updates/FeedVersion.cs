using System.Text.RegularExpressions;

namespace Granary.Core.Updates;

/// <summary>
/// The version the update feed names. The feed is not signed, so About shows and links a version only when it is a plain
/// one, as the release workflow makes them: "0.3.0", or "0.3.0-beta.1" with a label of at most 32 characters.
/// </summary>
internal static partial class FeedVersion
{
    public static bool IsPlain(string? version) => version is not null && Plain().IsMatch(version);

    // [0-9], since \d takes any script's digits; \z, since $ allows a line end after it.
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]{1,32})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Plain();
}
