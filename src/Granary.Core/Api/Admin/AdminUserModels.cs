using System.Text.Json.Nodes;

namespace Granary.Core.Api.Admin;

/// <summary>
/// A login account of <c>GET /api/v2/admin/users</c> and <c>GET /api/v2/admin/users/{id}</c>, with its own policy overrides.
/// Its name and address are personal data: they are shown, never logged.
/// </summary>
public sealed record AdminUser
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Username { get; init => field = value ?? ""; } = "";

    /// <summary>Empty when none is set.</summary>
    public string Email { get; init => field = value ?? ""; } = "";

    /// <summary>admin or user.</summary>
    public string Role { get; init => field = value ?? ""; } = "";

    /// <summary>The permissions given to the account itself, such as <c>marker_edit</c>.</summary>
    public IReadOnlyList<string> Permissions { get; init => field = value ?? []; } = [];

    public bool Enabled { get; init; }

    /// <summary>The account's own libraries; null takes its group's (or the server default), empty is none.</summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    /// <summary>The access group it belongs to; null for none (and always for an admin).</summary>
    public string? AccessGroupId { get; init; }

    /// <summary>It can sign in with a local password; false when a sign-in provider manages it.</summary>
    public bool PasswordLogin { get; init; }

    /// <summary>It holds a temporary password it must replace at its next sign-in.</summary>
    public bool PasswordChangeRequired { get; init; }

    public bool IsOwner { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    public bool IsAdmin => Role == AdminRoles.Admin;
}

public sealed record AdminUserCollection
{
    public IReadOnlyList<AdminUser> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary>The two roles an account takes.</summary>
public static class AdminRoles
{
    public const string Admin = "admin";
    public const string User = "user";
}

/// <summary>The permissions an administrator gives an account in Users, as the web's <c>lib/permissions.ts</c>.</summary>
public static class AdminPermissions
{
    public const string MarkerEdit = "marker_edit";
    public const string MetadataCuration = "metadata_curation";
}

/// <summary>What account administration offers on this server (<c>GET /api/v2/admin/users/capabilities</c>).</summary>
public sealed record AdminUserCapabilities
{
    public bool Available { get; init; }

    /// <summary>A new account can get its first profile in the same step.</summary>
    public bool DefaultProfile { get; init; }

    public bool AccessGroups { get; init; }

    public bool PasswordResetLink { get; init; }

    public bool PasswordResetEmail { get; init; }

    public bool OwnershipTransfer { get; init; }

    public bool LoginSessions { get; init; }
}

/// <summary>
/// An access group of <c>GET /api/v2/admin/access-groups</c>: its name, whether new accounts join it, and the policy its
/// members take where they do not override it.
/// </summary>
public sealed record AdminAccessGroup
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    public bool IsDefault { get; init; }

    /// <summary>Null for every library, empty for none.</summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    public string? MaxPlaybackQuality { get; init; }

    public int MaxStreams { get; init; }

    public int MaxTranscodes { get; init; }

    public int MaxRemoteStreamBitrateKbps { get; init; }

    public int MaxLocalStreamBitrateKbps { get; init; }

    public int MaxProfiles { get; init; }

    public bool TranscodeAllowed { get; init; }

    public bool AudioTranscodeAllowed { get; init; }

    public bool DownloadAllowed { get; init; }

    public bool DownloadTranscodeAllowed { get; init; }

    public bool RequestsAllowed { get; init; }

    /// <summary>What a member's unset fields resolve to.</summary>
    public AdminPolicyValues Policy => new()
    {
        LibraryIds = LibraryIds,
        MaxPlaybackQuality = MaxPlaybackQuality ?? "",
        MaxStreams = MaxStreams,
        MaxTranscodes = MaxTranscodes,
        MaxRemoteStreamBitrateKbps = MaxRemoteStreamBitrateKbps,
        MaxLocalStreamBitrateKbps = MaxLocalStreamBitrateKbps,
        MaxProfiles = MaxProfiles,
        TranscodeAllowed = TranscodeAllowed,
        AudioTranscodeAllowed = AudioTranscodeAllowed,
        DownloadAllowed = DownloadAllowed,
        DownloadTranscodeAllowed = DownloadTranscodeAllowed,
        RequestsAllowed = RequestsAllowed,
    };
}

/// <summary>
/// A resolved access policy: what an account's fields come to where it does not override them, from its group or the
/// server's built-in layers. 0 is unlimited for the limits; an empty quality has no ceiling.
/// </summary>
public sealed record AdminPolicyValues
{
    /// <summary>Null for every library, empty for none.</summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    public string MaxPlaybackQuality { get; init => field = value ?? ""; } = "";

    public int MaxStreams { get; init; }

    public int MaxTranscodes { get; init; }

    public int MaxRemoteStreamBitrateKbps { get; init; }

    public int MaxLocalStreamBitrateKbps { get; init; }

    public int MaxProfiles { get; init; }

    public bool TranscodeAllowed { get; init; }

    public bool AudioTranscodeAllowed { get; init; }

    public bool DownloadAllowed { get; init; }

    public bool DownloadTranscodeAllowed { get; init; }

    public bool RequestsAllowed { get; init; }
}

/// <summary>
/// The server's built-in policy layers (<c>GET /api/v2/admin/users/policy-defaults</c>): what an admin account's unset fields
/// resolve to, and a regular account's without an access group.
/// </summary>
public sealed record AdminPolicyDefaults
{
    public AdminPolicyValues? Admin { get; init; }

    public AdminPolicyValues? Ungrouped { get; init; }
}

/// <summary>
/// The server-wide switches the Users pages show beside what they govern, from the administrator settings: public signups
/// (the header's pill, the invite codes' note) and the two download switches (the note beside an account's downloads).
/// </summary>
public sealed record AdminServerSwitches(bool SignupsEnabled, bool DownloadsEnabled, bool DownloadConversionEnabled);

public sealed record AdminAccessGroupCollection
{
    public IReadOnlyList<AdminAccessGroup> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary>
/// A new account (<c>POST /api/v2/admin/users</c>), as the web's Create User sends it: a policy field is sent only where
/// the dialog overrides it (a null is left out and inherits; 0, an empty quality and no libraries are overrides). The
/// password is a secret: it is sent, never shown in a log, so this is a class whose text names nothing.
/// </summary>
public sealed class AdminUserDraft
{
    public required string Username { get; init; }

    public required string Email { get; init; }

    public required string Password { get; init; }

    /// <summary>True makes the password temporary; null (left out) when it is not.</summary>
    public bool? RequirePasswordChange { get; init; }

    public required string Role { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public bool CreateDefaultProfile { get; init; }

    /// <summary>Left out to let the server place the account in its default group.</summary>
    public string? AccessGroupId { get; init; }

    /// <summary>The account's own libraries; left out, its group's.</summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    public string? MaxPlaybackQuality { get; init; }

    public int? MaxStreams { get; init; }

    public int? MaxTranscodes { get; init; }

    public int? MaxRemoteStreamBitrateKbps { get; init; }

    public int? MaxLocalStreamBitrateKbps { get; init; }

    public int? MaxProfiles { get; init; }

    public bool? TranscodeAllowed { get; init; }

    public bool? AudioTranscodeAllowed { get; init; }

    public bool? DownloadAllowed { get; init; }

    public bool? DownloadTranscodeAllowed { get; init; }

    public bool? RequestsAllowed { get; init; }
}

/// <summary>The answer to a new account: its id.</summary>
public sealed record AdminUserCreated(string Id);

/// <summary>
/// A change to an account (<c>PUT /api/v2/admin/users/{id}</c>), as the web's sends one: only what changed. A part left
/// unset is left out and stays as it is; <see cref="SetsGroup"/> and <see cref="SetsLibraries"/> send theirs even when null,
/// which means "no group" and "the group's libraries". The password is a secret, so the text of a change names none.
/// </summary>
public sealed class AdminUserChange
{
    public string? Username { get; init; }

    public string? Email { get; init; }

    public string? Role { get; init; }

    public bool? Enabled { get; init; }

    public string? Password { get; init; }

    /// <summary>Only with <see cref="Password"/>: true makes it temporary.</summary>
    public bool? RequirePasswordChange { get; init; }

    public IReadOnlyList<string>? Permissions { get; init; }

    public bool SetsGroup { get; init; }

    public string? AccessGroupId { get; init; }

    public bool SetsLibraries { get; init; }

    public IReadOnlyList<string>? LibraryIds { get; init; }

    /// <summary>The body: its parts in the server's names, a null sent only where it means something.</summary>
    public JsonObject ToJson()
    {
        var body = new JsonObject();
        Add(body, "username", Username);
        Add(body, "email", Email);
        Add(body, "role", Role);
        if (Enabled is { } enabled)
        {
            body["enabled"] = enabled;
        }
        Add(body, "password", Password);
        if (RequirePasswordChange is { } temporary)
        {
            body["require_password_change"] = temporary;
        }
        if (Permissions is not null)
        {
            body["permissions"] = Array(Permissions);
        }
        if (SetsGroup)
        {
            body["access_group_id"] = AccessGroupId is null ? null : JsonValue.Create(AccessGroupId);
        }
        if (SetsLibraries)
        {
            body["library_ids"] = LibraryIds is null ? null : Array(LibraryIds);
        }
        return body;
    }

    private static void Add(JsonObject body, string name, string? value)
    {
        if (value is not null)
        {
            body[name] = value;
        }
    }

    private static JsonArray Array(IReadOnlyList<string> values) => [.. values.Select(value => (JsonNode?)JsonValue.Create(value))];
}

/// <summary>How a password reset link reaches the account (<c>POST /api/v2/admin/users/{id}/password-reset</c>).</summary>
public static class AdminResetDelivery
{
    public const string Email = "email";
    public const string Link = "link";
}

public sealed record AdminPasswordResetRequest(string Delivery);

/// <summary>
/// A password reset link issued: emailed, or to share. <see cref="ResetUrl"/> is a bearer credential shown once to the
/// administrator who asked for it; the record's text leaves it out.
/// </summary>
public sealed record AdminPasswordReset
{
    public string Delivery { get; init => field = value ?? ""; } = "";

    /// <summary>sent, failed_or_unknown or not_requested.</summary>
    public string DeliveryStatus { get; init => field = value ?? ""; } = "";

    public string? ResetUrl { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => $"AdminPasswordReset {{ Delivery = {Delivery}, DeliveryStatus = {DeliveryStatus} }}";
}

/// <summary>How many sign-ins Sign out everywhere ended.</summary>
public sealed record AdminSessionsRevoked(int Revoked);
