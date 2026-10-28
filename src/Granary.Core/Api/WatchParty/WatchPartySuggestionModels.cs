using System.Text.Json.Serialization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.WatchParty;

/// <summary><c>host_pick</c> (the host stages what plays) or <c>vote</c> (everyone suggests and votes, the host starts one).</summary>
public static class WatchPartySelectionModes
{
    public const string HostPick = "host_pick";
    public const string Vote = "vote";
}

/// <summary>
/// A title a member put forward, with its tally. Over HTTP <see cref="VotedByMe"/> is this profile's own vote; the
/// socket's <c>suggestions_update</c> sends it false for everyone, so the client keeps its own.
/// </summary>
public sealed record WatchPartySuggestion
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string RoomId { get; init => field = value ?? ""; } = "";

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string SuggesterUserId { get; init => field = value ?? ""; } = "";

    public string SuggesterProfileId { get; init => field = value ?? ""; } = "";

    public string ContentId { get; init => field = value ?? ""; } = "";

    /// <summary><c>movie</c> or <c>episode</c>: a series is suggested by one of its episodes.</summary>
    public string ContentType { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>The year of a movie, or "Series · S1 E2" of an episode.</summary>
    public string Subtitle { get; init => field = value ?? ""; } = "";

    /// <summary>The poster the suggesting member's client sent; not checked by the server.</summary>
    public string PosterUrl { get; init => field = value ?? ""; } = "";

    public string Note { get; init => field = value ?? ""; } = "";

    public int VoteCount { get; init; }

    public bool VotedByMe { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The suggesting member as the room's members are keyed: <c>user:profile</c>.</summary>
    public string SuggesterKey => SuggesterUserId + ":" + SuggesterProfileId;
}

/// <summary>A page of <c>GET …/suggestions</c>, in creation order.</summary>
public sealed record WatchPartySuggestionPage
{
    public IReadOnlyList<WatchPartySuggestion> Items { get; init => field = value ?? []; } = [];

    public WatchPartyPageInfo? Page { get; init; }
}

public sealed record WatchPartyPageInfo(bool HasMore, string? NextCursor);

/// <summary>The body of <c>POST …/suggestions</c>.</summary>
/// <param name="SuggestionId">A UUID the client picks: a retry with it answers the same suggestion.</param>
/// <param name="ContentType"><c>movie</c> or <c>episode</c>.</param>
public sealed record WatchPartySuggestionCreate(string SuggestionId, string ContentId, string ContentType, string Title, string? Subtitle = null,
    string? PosterUrl = null, string? Note = null);

/// <summary>The answer of <c>POST …/suggestions</c>.</summary>
public sealed record WatchPartySuggestionReceipt(string SuggestionId);

/// <summary>The body of <c>POST …/suggestions/promote</c>.</summary>
public sealed record WatchPartyPromote(string SuggestionId);

/// <summary>The body of <c>PATCH …/selection-mode</c>.</summary>
public sealed record WatchPartySelectionModeChange(string SelectionMode);

/// <summary>
/// <c>GET …/picker</c>: the rows the room's picker leads with, computed over its connected members: what two or more
/// of them are part way through, and what is on anyone's watchlist.
/// </summary>
public sealed record WatchPartyPicker
{
    public IReadOnlyList<WatchPartyPickerEntry> ContinueTogether { get; init => field = value ?? []; } = [];

    public IReadOnlyList<WatchPartyPickerEntry> WatchlistUnion { get; init => field = value ?? []; } = [];
}

/// <summary>A picker card and the members it applies to.</summary>
public sealed record WatchPartyPickerEntry
{
    public CatalogItem Item { get; init => field = value ?? new CatalogItem(); } = new();

    public IReadOnlyList<WatchPartyPickerMember> Members { get; init => field = value ?? []; } = [];

    /// <summary>For a series: the episode most of them would play next.</summary>
    public WatchPartyPickerNextUp? NextUp { get; init; }
}

public sealed record WatchPartyPickerMember
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string UserId { get; init => field = value ?? ""; } = "";

    public string ProfileId { get; init => field = value ?? ""; } = "";

    public string DisplayName { get; init => field = value ?? ""; } = "";

    public double? PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    public string Key => UserId + ":" + ProfileId;
}

public sealed record WatchPartyPickerNextUp
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    public int SeasonNumber { get; init; }

    public int EpisodeNumber { get; init; }

    public string? Title { get; init; }

    public int MemberCount { get; init; }
}

/// <summary>The body of <c>POST …/member-state</c>: up to 200 content ids.</summary>
public sealed record WatchPartyMemberStateQuery(IReadOnlyList<string> ContentIds);

/// <summary>What each connected member has watched of the asked-for titles, in the order asked.</summary>
public sealed record WatchPartyMemberStates
{
    public IReadOnlyList<WatchPartyItemMemberState> Items { get; init => field = value ?? []; } = [];
}

public sealed record WatchPartyItemMemberState
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<WatchPartyMemberWatchState> Members { get; init => field = value ?? []; } = [];
}

public sealed record WatchPartyMemberWatchState
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string UserId { get; init => field = value ?? ""; } = "";

    public string ProfileId { get; init => field = value ?? ""; } = "";

    /// <summary><c>unseen</c>, <c>in_progress</c> or <c>watched</c>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    public double? PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    public bool OnWatchlist { get; init; }

    public string Key => UserId + ":" + ProfileId;
}
