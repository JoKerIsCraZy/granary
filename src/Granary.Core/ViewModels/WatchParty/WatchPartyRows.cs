using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.WatchParty;
using Granary.Core.Texts;
using Granary.Core.WatchParty;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// One colour per member, by position in the room's member list (host first), as the web assigns them
/// (<c>room/members.ts</c>): the same member has the same colour in the people list, the picker's badges and the
/// "who's seen it" list. The page maps <see cref="Tint"/> to its palette.
/// </summary>
public static class WatchPartyMembers
{
    public const int TintCount = 8;

    /// <summary>"EL" for Elena, "MS" for Maya Smith, "?" for no name.</summary>
    public static string Initials(string name)
    {
        string[] parts = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => (parts[0].Length > 2 ? parts[0][..2] : parts[0]).ToUpperInvariant(),
            _ => string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant(),
        };
    }

    /// <summary>Each member's colour, keyed <c>user:profile</c>.</summary>
    public static IReadOnlyDictionary<string, int> Tints(IReadOnlyList<WatchPartyMember> members)
    {
        var tints = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < members.Count; i++)
        {
            tints.TryAdd(members[i].Key, i % TintCount);
        }
        return tints;
    }
}

/// <summary>A member in the people list: "EL · Elena · HOST · (you) · Connected".</summary>
/// <param name="State">Watching, Buffering or Paused while a title plays; Ready or Connected in the lobby.</param>
/// <param name="IsWaiting">The room waits for its members to buffer: the dot goes amber.</param>
public sealed record WatchPartyMemberRow(string Name, bool IsHost, bool IsSelf, string State, bool ShowsReadyTick, bool IsWaiting, int Tint)
{
    public string Initials => WatchPartyMembers.Initials(Name);
}

/// <summary>A viewer in the player's Watch Party panel: "Elena (you) · Ready".</summary>
/// <param name="State">Buffering, Syncing, Ready or Connected, as the member's own player reports it.</param>
public sealed record WatchPartyViewerRow(string Key, string Name, string State);

/// <summary>A line of the activity feed: "2m · Elena joined". Its text fills in once the title it names is read.</summary>
public sealed partial class WatchPartyActivityRow(long id, DateTimeOffset at) : ObservableObject
{
    public long Id { get; } = id;

    public DateTimeOffset At { get; } = at;

    [ObservableProperty]
    public partial string Time { get; set; } = "";

    [ObservableProperty]
    public partial string Text { get; set; } = "";
}

/// <summary>A member's badge on a picker card: their initials in their colour.</summary>
public sealed record WatchPartyBadge(string Initials, int Tint, string Name);

/// <summary>A card on the room's shelf: a movie or a series, with who in the room it is about.</summary>
/// <param name="Caption">The year, "Movie" or "Series", or for a series in progress the episode the room is on ("S2 E3").</param>
/// <param name="Progress">For a title in progress: how far the one furthest behind is, 0 to 1.</param>
/// <param name="Season">For a series in progress: the season the room is on.</param>
/// <param name="PosterPath">The poster as the server named it, for a suggestion's <c>poster_url</c>.</param>
/// <remarks>Only its ring changes, so choosing a card never rebuilds a row the viewer scrolled.</remarks>
public sealed partial class WatchPartyShelfCard(string contentId, string type, string title, string caption, Uri? posterUrl, string? posterPath,
    IReadOnlyList<WatchPartyBadge> badges, int moreMembers, string memberNames, double? progress, int? season, int? year) : ObservableObject
{
    public string ContentId { get; } = contentId;

    public string Type { get; } = type;

    public string Title { get; } = title;

    public string Caption { get; } = caption;

    public Uri? PosterUrl { get; } = posterUrl;

    public string? PosterPath { get; } = posterPath;

    public IReadOnlyList<WatchPartyBadge> Badges { get; } = badges;

    public int MoreMembers { get; } = moreMembers;

    public string MemberNames { get; } = memberNames;

    public double? Progress { get; } = progress;

    public int? Season { get; } = season;

    public int? Year { get; } = year;

    /// <summary>The title on the stage, ringed.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool IsSeries => Type == Api.Catalog.CatalogItemTypes.Series;

    public bool HasMemberNames => MemberNames.Length > 0;

    public bool HasBadges => Badges.Count > 0;

    public bool HasMoreMembers => MoreMembers > 0;

    public string MoreText => AppText.Get("web.watchTogether.picker.moreMembers", ("count", MoreMembers));

    public bool HasProgress => Progress is not null;

    public double ProgressPercent => (Progress ?? 0) * 100;
}

/// <summary>A titled row of the shelf: "ON YOUR WATCHLISTS · anyone in the room".</summary>
public sealed record WatchPartyShelfRow(string Key, string Title, string Hint, IReadOnlyList<WatchPartyShelfCard> Cards)
{
    public bool HasHint => Hint.Length > 0;
}

/// <summary>
/// A suggestion as the lists show it: the vote toggle, poster, title and subtitle, who put it forward, the vote bar and
/// count, and the host's (or its suggester's) actions.
/// </summary>
/// <remarks>One row per suggestion, changed in place as the list moves: a vote does not reload its poster or lose focus.</remarks>
public sealed partial class WatchPartySuggestionRow(WatchPartySuggestion suggestion) : ObservableObject
{
    public string Id { get; } = suggestion.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(HasSubtitle), nameof(VotedByMe), nameof(By), nameof(VotesText), nameof(VoteName),
        nameof(DeleteName))]
    public partial WatchPartySuggestion Suggestion { get; set; } = suggestion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(By))]
    public partial string SuggesterName { get; set; } = "";

    /// <summary>Its votes against the leader's, 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharePercent))]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial bool IsLeading { get; set; }

    [ObservableProperty]
    public partial bool ShowsVotes { get; set; }

    [ObservableProperty]
    public partial bool CanVote { get; set; }

    [ObservableProperty]
    public partial bool CanPromote { get; set; }

    /// <summary>"Play this" in a vote room, "Queue this" in a host-pick lobby.</summary>
    [ObservableProperty]
    public partial string PromoteLabel { get; set; } = "";

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    [ObservableProperty]
    public partial Uri? PosterUrl { get; set; }

    public string Title => Suggestion.Title;

    public string Subtitle => Suggestion.Subtitle;

    public bool HasSubtitle => Suggestion.Subtitle.Length > 0;

    public bool VotedByMe => Suggestion.VotedByMe;

    /// <summary>Who suggested it, and their note as the web puts it after the name ("Maya: “Friday classic”").</summary>
    public string By => Suggestion.Note.Length > 0
        ? SuggesterName + AppText.Get("web.watchTogether.suggestions.note", ("note", Suggestion.Note))
        : SuggesterName;

    public string VotesText => AppText.Get("web.watchTogether.suggestions.votes", ("count", Suggestion.VoteCount),
        ("count_text", AppText.Number(Suggestion.VoteCount)));

    public double SharePercent => Share * 100;

    public string VoteName => VotedByMe
        ? AppText.Get("web.watchTogether.suggestions.unvote", ("title", Title))
        : AppText.Get("web.watchTogether.suggestions.vote", ("title", Title));

    public string DeleteName => AppText.Get("web.watchTogether.suggestions.remove", ("title", Title));
}

/// <summary>A member in the candidate's "who's seen it" list.</summary>
/// <param name="State">"Hasn't seen it", "Watched", "45% in" or "In progress".</param>
public sealed record WatchPartySeenRow(string Name, int Tint, string State, bool OnWatchlist)
{
    public string Initials => WatchPartyMembers.Initials(Name);
}

/// <summary>Whether a recent room is still on, as its row says it.</summary>
public enum WatchPartyRecentStatus
{
    /// <summary>Not asked yet: "Checking…".</summary>
    Checking,

    Live,

    /// <summary>The server could not be asked: "Status unavailable", and it may be rejoined.</summary>
    Unknown,

    Ended,
}

/// <summary>A room on the hub's "Your recent parties".</summary>
public sealed partial class WatchPartyRecentRow(WatchPartyRecentRoom room) : ObservableObject
{
    public WatchPartyRecentRoom Room { get; } = room;

    /// <summary>The room's name as stored (English, as the web stores it), or "Watch Party".</summary>
    public string Title => Room.Title ?? AppText.Get("watchParty.title");

    public string Role => Room.IsHost ? AppText.Get("web.watchTogether.hub.recentHosted") : AppText.Get("web.watchTogether.hub.recentJoined");

    public string Code => Room.Code;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(CanRejoin), nameof(IsDimmed), nameof(EndLabel), nameof(StatusText))]
    public partial WatchPartyRecentStatus Status { get; set; } = room.Ended ? WatchPartyRecentStatus.Ended : WatchPartyRecentStatus.Checking;

    /// <summary>"Live", "Checking…", "Status unavailable" or "Ended".</summary>
    public string StatusText => Status switch
    {
        WatchPartyRecentStatus.Live => AppText.Get("web.watchTogether.hub.recentLive"),
        WatchPartyRecentStatus.Checking => AppText.Get("web.watchTogether.hub.recentChecking"),
        WatchPartyRecentStatus.Unknown => AppText.Get("web.watchTogether.hub.recentUnknown"),
        _ => AppText.Get("web.watchTogether.hub.recentEnded"),
    };

    public bool IsLive => Status == WatchPartyRecentStatus.Live;

    public bool CanRejoin => Status is WatchPartyRecentStatus.Live or WatchPartyRecentStatus.Unknown;

    public bool IsDimmed => !IsLive;

    /// <summary>"ENDED" where a row cannot be rejoined, nothing while it is checked.</summary>
    public string EndLabel => CanRejoin || Status == WatchPartyRecentStatus.Checking ? "" : AppText.Upper(AppText.Get("web.watchTogether.hub.recentEnded"));
}
