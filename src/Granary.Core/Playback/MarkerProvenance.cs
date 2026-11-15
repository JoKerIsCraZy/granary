using System.Globalization;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>
/// Where a marked part came from, as the server recorded it beside the range (docs/api-contract.md, 11.1), and the line
/// the skip pill shows above itself, as the web's <c>lib/markerProvenance.ts</c> words it. The server keeps one origin
/// per file and kind, so every occurrence of a kind shares it. The confidence has no common scale: Silo's detection and
/// each provider rate on their own, so it only means something next to its source.
/// </summary>
/// <param name="Confidence">0 to 1; null when nobody rated the part, which is never shown as 0 %.</param>
public sealed record MarkerProvenance(string? Source, string? Provider, double? Confidence)
{
    public static MarkerProvenance None { get; } = new(null, null, null);

    /// <summary>The provenance of a kind's range; <see cref="None"/> without one.</summary>
    public static MarkerProvenance Of(WatchMarker? marker) =>
        marker is null ? None : new MarkerProvenance(marker.Source, marker.Provider, marker.Confidence);

    /// <summary>
    /// "Detected" for Silo's own analysis, "Edited" for a person, a provider by its name; null when nothing is known. A
    /// provider without a friendly name shows its own id: an odd answer is better than none, as the line exists to say
    /// where the part came from.
    /// </summary>
    public string? SourceLabel => Source switch
    {
        "scanner" => Detected,
        "manual" => "Edited",
        "plugin" or "online" => ProviderName(Provider),
        // No source recorded (an older server, or a row from before it kept one): a provider is still worth naming.
        _ => string.IsNullOrEmpty(Provider) ? null : ProviderName(Provider),
    };

    /// <summary>"20%"; null for an unrated part. Out-of-range ratings are clamped, half a percent rounds up, as on the web.</summary>
    public string? ConfidencePercent => Confidence is { } confidence && double.IsFinite(confidence)
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(Math.Clamp(confidence, 0, 1) * 100, MidpointRounding.AwayFromZero)}%")
        : null;

    /// <summary>
    /// The line above the skip pill: the confidence, the source, or both ("20% · Detected"). The confidence comes first,
    /// as it is the number the viewer judges, and the source qualifies it. Null when there is nothing true to say, so the
    /// pill keeps its shape.
    /// </summary>
    public string? Line(bool showConfidence, bool showSource)
    {
        string?[] parts = [showConfidence ? ConfidencePercent : null, showSource ? SourceLabel : null];
        var shown = parts.OfType<string>().ToList();
        return shown.Count > 0 ? string.Join(" · ", shown) : null;
    }

    private const string Detected = "Detected";

    // Provider ids are not uniform: a plugin's comes as plugin:<installation id>:<capability id>, where only the capability
    // means anything to a viewer; a built-in one is built_in; another may be its own slug.
    private static string ProviderName(string? provider)
    {
        string id = provider?.Trim() ?? "";
        if (id.Length == 0)
        {
            return "Online";
        }
        if (KnownName(id) is { } named)
        {
            return named;
        }
        return PluginCapability(id) is { } capability ? KnownName(capability) ?? capability : id;
    }

    private static string? KnownName(string id) => id switch
    {
        "introdb" => "TheIntroDB",
        "built_in" => Detected,
        _ => null,
    };

    // The web's /^plugin:\d+:(.+)$/: the capability after a numeric installation id, or null.
    private static string? PluginCapability(string id)
    {
        const string Prefix = "plugin:";
        if (!id.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        int digits = Prefix.Length;
        while (digits < id.Length && char.IsAsciiDigit(id[digits]))
        {
            digits++;
        }
        bool hasInstallation = digits > Prefix.Length && digits < id.Length - 1 && id[digits] == ':';
        return hasInstallation ? id[(digits + 1)..] : null;
    }
}
