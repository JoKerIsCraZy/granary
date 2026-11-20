namespace Granary.Core.Api.Admin;

/// <summary>
/// A signup invite code of <c>GET /api/v2/admin/invite-codes</c>. The code is a secret anyone may sign up with while public
/// signups are on: it is shown and copied, never logged, so the record's text leaves it out.
/// </summary>
public sealed record AdminInviteCode
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Code { get; init => field = value ?? ""; } = "";

    /// <summary>Empty when none was given.</summary>
    public string Label { get; init => field = value ?? ""; } = "";

    public int MaxUses { get; init; }

    public int UseCount { get; init; }

    public bool Enabled { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public override string ToString() => $"AdminInviteCode {{ Id = {Id}, UseCount = {UseCount}, MaxUses = {MaxUses}, Enabled = {Enabled} }}";
}

public sealed record AdminInviteCodeCollection
{
    public IReadOnlyList<AdminInviteCode> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary>A new code, as the web's Create Invite Code sends it: the label is sent even when empty, never as null.</summary>
public sealed class AdminInviteCodeDraft
{
    public required string Code { get; init; }

    public required string Label { get; init; }

    public required int MaxUses { get; init; }

    public override string ToString() => $"AdminInviteCodeDraft {{ MaxUses = {MaxUses} }}";
}

/// <summary>Turns a code on or off (<c>PUT /api/v2/admin/invite-codes/{id}</c>).</summary>
public sealed record AdminInviteCodeChange(bool Enabled);

/// <summary>More uses for a code (<c>POST /api/v2/admin/invite-codes/{id}/top-up</c>).</summary>
public sealed record AdminInviteCodeTopUp(int AdditionalUses);
