namespace Granary.Core.Api.Profiles;

/// <summary><c>GET /api/v2/profiles</c>: the household and what the server lets a profile carry.</summary>
public sealed record ProfileCollection(IReadOnlyList<Profile> Items, bool AvatarUploadEnabled)
{
    /// <summary>Profiles take and enforce <c>max_advisory_age</c>; never send it otherwise, an older server refuses the write.</summary>
    public bool MaxAdvisoryAgeSupported { get; init; }

    /// <summary>Profiles take <c>require_advisory_age</c>, which came after the age limit itself.</summary>
    public bool RequireAdvisoryAgeSupported { get; init; }

    /// <summary>Profiles take <c>requests_allowed</c>.</summary>
    public bool RequestsAllowedSupported { get; init; }

    /// <summary>
    /// How many profiles the account may have; a create past it answers 409. Null when the server did not say, and a new
    /// profile is still offered (the web's <c>canAddProfile</c>).
    /// </summary>
    public int? MaxProfiles { get; init; }

    /// <summary>There is room for another profile.</summary>
    public bool HasRoom => MaxProfiles is not { } limit || Items.Count < limit;
}

/// <summary>A household profile on the signed-in account (only the members Granary uses).</summary>
public sealed record Profile(
    string Id,
    string Name,
    bool IsPrimary,
    bool HasPin,
    bool IsChild,
    string Avatar,
    string AvatarSource,
    string? AvatarUrl,
    string Language,
    string SubtitleLanguage,
    string SubtitleMode)
{
    /// <summary>The content-rating ceiling, such as <c>PG-13</c>; empty or null for none.</summary>
    public string? MaxContentRating { get; init; }

    /// <summary>The advisory-age limit, 1 to 21; null for none.</summary>
    public int? MaxAdvisoryAge { get; init; }

    /// <summary>Titles without an advisory age are hidden too; only with <see cref="MaxAdvisoryAge"/>.</summary>
    public bool RequireAdvisoryAge { get; init; }

    /// <summary>As sent; absent on a server that predates the switch. Read <see cref="MayRequest"/>.</summary>
    public bool? RequestsAllowed { get; init; }

    /// <summary>The profile may use requests: allowed unless the server says otherwise.</summary>
    public bool MayRequest => RequestsAllowed ?? true;

    /// <summary>The profile sees only <see cref="AllowedLibraryIds"/>. Never on the primary profile.</summary>
    public bool LibraryRestrictionsEnabled { get; init; }

    /// <summary>The libraries a restricted profile sees; null when the server sent none.</summary>
    public IReadOnlyList<string>? AllowedLibraryIds { get; init; }

    /// <summary>The playback ceiling: <c>1080p</c>, <c>2160p</c>, or empty or null for none; older profiles may carry other values.</summary>
    public string? MaxPlaybackQuality { get; init; }
}

/// <summary><c>POST /api/v2/profiles/{id}/verify-pin</c> body.</summary>
public sealed record ProfilePinCheck(string Pin)
{
    public override string ToString() => "ProfilePinCheck { Pin = <redacted> }";
}

/// <summary>Answer of verify-pin. A wrong PIN is <c>valid: false</c>, not an error.</summary>
public sealed record ProfileVerification(bool Valid, string? ProfileToken, DateTimeOffset? ExpiresAt)
{
    public override string ToString() => $"ProfileVerification {{ Valid = {Valid} }}";
}
