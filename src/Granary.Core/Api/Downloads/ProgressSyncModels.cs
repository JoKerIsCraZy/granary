namespace Granary.Core.Api.Downloads;

/// <summary><c>POST /api/v2/sync/progress</c> (downloads-api.md, 7.1): positions recorded without a playback session, 100 at most.</summary>
public sealed record ProgressSyncBody(IReadOnlyList<ProgressSyncItem> Items);

/// <summary>
/// Where the viewer was in a film or an episode, in whole milliseconds, and when this PC recorded it: the server keeps the
/// later of this and what it has (last write wins on <paramref name="UpdatedAt"/>), and decides itself whether the title
/// counts as watched. <paramref name="DurationMs"/> is 0 when the length is not known.
/// </summary>
public sealed record ProgressSyncItem(string MediaItemId, long PositionMs, long DurationMs, DateTimeOffset UpdatedAt);

/// <summary>The answer: one result for each item sent, by its place in the request.</summary>
public sealed record ProgressSyncResult
{
    public IReadOnlyList<ProgressSyncItemResult> Items { get; init => field = value ?? []; } = [];
}

/// <summary>One item's result: <c>success</c> (taken, or passed over as older) or <c>failure</c>.</summary>
public sealed record ProgressSyncItemResult(int Index, string? MediaItemId, string? Status)
{
    public const string Success = "success";
}

/// <summary>One page of <c>GET /api/v2/progress</c> (downloads-api.md, 7.2), newest change first.</summary>
public sealed record ProgressListPage(IReadOnlyList<ProgressListItem> Items, DownloadPageInfo? Page)
{
    public IReadOnlyList<ProgressListItem> Items { get; init; } = Items ?? [];
}

/// <summary>
/// The profile's progress in one film or episode as the server has it. A title it counts as watched is
/// <paramref name="Completed"/> with its position back at 0; <paramref name="UpdatedAt"/> is when the device that reported
/// it recorded it.
/// </summary>
public sealed record ProgressListItem(string? MediaItemId, double PositionSeconds, double? DurationSeconds, bool Completed, DateTimeOffset UpdatedAt);
