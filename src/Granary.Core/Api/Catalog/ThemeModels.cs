using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// An item's theme music as the server resolved it (<c>themes</c> on the item detail, docs/api-contract.md 7.19): one owner's
/// ordered set, a season's own or its series', an episode's season's or series'. An empty set keeps the item's id.
/// </summary>
public sealed record ThemeSongSet
{
    /// <summary>The movie, series or season the themes belong to; a season that inherits names its series.</summary>
    public string OwnerId { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<ThemeSong> Items { get; init => field = value ?? []; } = [];
}

/// <summary>One local theme; paths are never sent.</summary>
public sealed record ThemeSong
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string? Title { get; init; }

    public long? DurationSeconds { get; init; }

    public string? Container { get; init; }
}

/// <summary><c>GET /api/v2/catalog/themes/capabilities</c>: whether the server serves themes to this login.</summary>
public sealed record ThemeSongsCapability
{
    /// <summary><c>available</c>, <c>disabled</c>, <c>not_configured</c> or <c>unsupported</c>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>Whether this account and profile may use themes; absent reads as allowed.</summary>
    public bool? Allowed { get; init; }

    /// <summary>A theme this PC cannot decode can be converted to AAC.</summary>
    public bool Transcode { get; init; }

    public long? GrantLifetimeSeconds { get; init; }

    /// <summary>Themes play: the server serves them and lets this login use them, as the apps read it.</summary>
    [JsonIgnore]
    public bool IsAvailable => State == "available" && Allowed != false;
}

/// <summary>A container and codec this PC decodes; an empty codec accepts any codec in the container.</summary>
public sealed record ThemeAudioFormat(string Container, string AudioCodec);

/// <summary>The body of <c>POST /api/v2/catalog/items/{id}/themes/{theme_id}/playback</c>.</summary>
public sealed record ThemePlaybackRequest(IReadOnlyList<ThemeAudioFormat> AcceptedFormats);

/// <summary>
/// A short-lived grant to play one theme. <see cref="Url"/> is a credential: it is never logged or kept, so
/// <see cref="ToString"/> leaves it out.
/// </summary>
public sealed record ThemePlayback
{
    /// <summary>Relative to the server, or absolute on a proxy node's origin; it carries its own token.</summary>
    public string Url { get; init => field = value ?? ""; } = "";

    public string? ExpiresAt { get; init; }

    /// <summary><c>original</c>, or <c>converted</c>: progressive AAC with no length and no byte ranges.</summary>
    public string? Delivery { get; init; }

    public string? ContentType { get; init; }

    /// <summary>A converted stream cannot be played again from its start: a loop asks for a new grant.</summary>
    [JsonIgnore]
    public bool IsConverted => Delivery == "converted";

    public override string ToString() => $"ThemePlayback {{ Delivery = {Delivery}, ContentType = {ContentType}, ExpiresAt = {ExpiresAt} }}";
}
