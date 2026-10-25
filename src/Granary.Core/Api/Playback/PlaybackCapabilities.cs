using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

/// <summary>
/// <c>GET /api/v2/playback/capabilities</c> (docs/api-contract.md, 8.3). Play only while <see cref="IsUsable"/>
/// holds; there is no negotiation and no fallback protocol. Refetch it after a 409 <c>installation_changed</c>.
/// </summary>
public sealed record PlaybackCapabilities
{
    /// <summary>Opaque; changes when anything in the document does.</summary>
    public string Revision { get; init => field = value ?? ""; } = "";

    /// <summary><c>available</c>, <c>disabled</c>, <c>not_configured</c> or <c>unsupported</c>; anything else is unavailable.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>Whether this account and profile may play.</summary>
    public bool Allowed { get; init; }

    /// <summary>The value every playback mutation carries as <c>installation_id</c>; absent when playback is not configured.</summary>
    public string? InstallationId { get; init; }

    public IReadOnlyList<int> ProtocolVersions { get; init => field = value ?? []; } = [];

    /// <summary>Feature tokens (<see cref="PlaybackFeatures"/>); unknown ones are ignored.</summary>
    public IReadOnlyList<string> Features { get; init => field = value ?? []; } = [];

    /// <summary>Server-side delivery values (<see cref="PlaybackDeliveries"/>), not the client's delivery classes.</summary>
    public IReadOnlyList<string> Deliveries { get; init => field = value ?? []; } = [];

    /// <summary>With <c>connection_speed_v1</c>: where a speed test downloads from.</summary>
    public string? BandwidthProbeUrl { get; init; }

    /// <summary>With <c>connection_speed_v1</c>: the rate a quick speed test has to prove.</summary>
    public int? BandwidthProbeTargetKbps { get; init; }

    /// <summary>With <c>connection_speed_v1</c>: false when the administrator turned automatic speed tests off.</summary>
    public bool? BandwidthProbeAutomatic { get; init; }

    /// <summary>With <c>connection_speed_v1</c>: how long a speed reading stays current.</summary>
    public int? BandwidthProbeMaxAgeSeconds { get; init; }

    /// <summary>
    /// The 8.3 gate: <c>state</c> is <c>available</c>, <c>allowed</c> is true, protocol 3 is offered, the three
    /// features the client depends on (<c>playback_plan_v3</c>, <c>neutral_playback_v3_contract_v1</c>,
    /// <c>sequenced_progress_v1</c>) are present, and there is an installation id to send with every mutation.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable =>
        State == "available"
        && Allowed
        && !string.IsNullOrEmpty(InstallationId)
        && ProtocolVersions.Contains(PlaybackProtocol.Version)
        && HasFeature(PlaybackFeatures.PlaybackPlanV3)
        && HasFeature(PlaybackFeatures.NeutralPlaybackV3ContractV1)
        && HasFeature(PlaybackFeatures.SequencedProgressV1);

    /// <summary>Whether the document lists <paramref name="feature"/>, compared as the server does (trimmed, ignoring case).</summary>
    public bool HasFeature(string feature) => PlaybackProtocol.HasFeature(Features, feature);
}

/// <summary>The protocol constants the requests and the gates share.</summary>
public static class PlaybackProtocol
{
    /// <summary>The only playback protocol: <c>protocol_version</c> in every body and in <c>client_playback_context</c>.</summary>
    public const int Version = 3;

    internal static bool HasFeature(IReadOnlyList<string> features, string feature) =>
        features.Any(f => string.Equals(f?.Trim(), feature, StringComparison.OrdinalIgnoreCase));
}
