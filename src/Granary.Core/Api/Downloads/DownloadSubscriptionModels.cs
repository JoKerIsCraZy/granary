namespace Granary.Core.Api.Downloads;

/// <summary>
/// A series monitor (<c>DownloadSubscription</c>, downloads-api.md, 8): it keeps a series downloaded on this PC for one account
/// and profile. The server registers the episodes in its scope only when Granary syncs it; <see cref="Etag"/> guards every
/// change and names the monitor in a sync.
/// </summary>
public sealed record DownloadSubscription
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string SeriesId { get; init => field = value ?? ""; } = "";

    /// <summary>One of <see cref="SubscriptionModes"/>.</summary>
    public string Mode { get; init => field = value ?? ""; } = "";

    /// <summary>The season a <c>latest_season</c> monitor counts from.</summary>
    public int? TargetSeason { get; init; }

    /// <summary>The seasons a <c>specific_seasons</c> monitor takes.</summary>
    public IReadOnlyList<int> SeasonNumbers { get; init => field = value ?? []; } = [];

    /// <summary>Finished episodes are deleted from this PC; the server's sync passes over the ones the profile finished.</summary>
    public bool DeleteWatched { get; init; }

    /// <summary>The most the series' downloads take on this PC; 0 for no limit. Granary holds to it, the server only roughly.</summary>
    public long MaxStorageBytes { get; init; }

    /// <summary>The preset its episodes are registered in; absent on a server without <c>monitor_quality</c>, which takes originals.</summary>
    public string? Quality { get; init; }

    /// <summary>False while it is paused: it registers nothing.</summary>
    public bool Active { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The validator a change sends back as <c>If-Match</c>, and a sync names.</summary>
    public string Etag { get; init => field = value ?? ""; } = "";

    /// <summary>It can be kept and changed: it has an id, a series and a validator.</summary>
    public bool IsUsable => Id.Length > 0 && SeriesId.Length > 0 && Etag.Length > 0;
}

/// <summary>One page of the monitor list, newest first.</summary>
public sealed record DownloadSubscriptionPage(IReadOnlyList<DownloadSubscription> Items, DownloadPageInfo? Page)
{
    public IReadOnlyList<DownloadSubscription> Items { get; init; } = Items ?? [];
}

/// <summary>
/// <c>POST /api/v2/downloads/subscriptions</c>. <see cref="DeleteWatched"/> and <see cref="MaxStorageBytes"/> are always sent;
/// <see cref="SeasonNumbers"/> only for <c>specific_seasons</c>, <see cref="Quality"/> only to a server with <c>monitor_quality</c>.
/// </summary>
public sealed record CreateSubscriptionBody
{
    public required string SeriesId { get; init; }

    public required string Mode { get; init; }

    public IReadOnlyList<int>? SeasonNumbers { get; init; }

    public bool DeleteWatched { get; init; }

    public long MaxStorageBytes { get; init; }

    public string? Quality { get; init; }
}

/// <summary>
/// <c>PATCH /api/v2/downloads/subscriptions/{id}</c>: only what changes. A member left null is left out, never sent as null,
/// which the server refuses.
/// </summary>
public sealed record UpdateSubscriptionBody
{
    public string? Mode { get; init; }

    public IReadOnlyList<int>? SeasonNumbers { get; init; }

    public bool? DeleteWatched { get; init; }

    public long? MaxStorageBytes { get; init; }

    public bool? Active { get; init; }

    /// <summary>Sent only when it changes: the server checks the account's transcode permission for any quality it gets.</summary>
    public string? Quality { get; init; }
}

/// <summary>The body of every page of one sync: the monitor and the validator read before the first page.</summary>
public sealed record SubscriptionSyncBody(string SubscriptionId, string Etag);

/// <summary>One page of a sync: how many episodes it registered, and whether more follow.</summary>
public sealed record SubscriptionSyncPage
{
    public string SubscriptionId { get; init => field = value ?? ""; } = "";

    public int Registered { get; init; }

    public int Examined { get; init; }

    public DownloadPageInfo? Page { get; init; }
}

/// <summary>What a monitor takes (downloads-api.md, 8.1).</summary>
public static class SubscriptionModes
{
    /// <summary>Every episode now, then new ones as they air.</summary>
    public const string All = "all";

    /// <summary>Only episodes that air from now on; the Apple app's default.</summary>
    public const string Future = "future";

    /// <summary>The latest season and anything newer.</summary>
    public const string LatestSeason = "latest_season";

    /// <summary>The seasons named; another client's choice, which Granary keeps but does not offer.</summary>
    public const string SpecificSeasons = "specific_seasons";

    /// <summary>Every mode, in the order the Apple app lists the ones it offers.</summary>
    public static IReadOnlyList<string> Known { get; } = [All, Future, LatestSeason, SpecificSeasons];
}
