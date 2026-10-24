namespace Granary.Core.Api.Profiles;

/// <summary><c>GET /api/v2/profiles</c>.</summary>
public sealed record ProfileCollection(IReadOnlyList<Profile> Items, bool AvatarUploadEnabled);

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
    string SubtitleMode);

/// <summary><c>POST /api/v2/profiles/{id}/verify-pin</c> body.</summary>
public sealed record ProfilePinCheck(string Pin);

/// <summary>Answer of verify-pin. A wrong PIN is <c>valid: false</c>, not an error.</summary>
public sealed record ProfileVerification(bool Valid, string? ProfileToken, DateTimeOffset? ExpiresAt);
