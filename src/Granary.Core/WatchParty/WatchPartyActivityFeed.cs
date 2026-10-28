using Granary.Core.Api.WatchParty;

namespace Granary.Core.WatchParty;

/// <summary>Something this client saw happen in the room.</summary>
/// <param name="Kind">One of <see cref="WatchPartyActivityKinds"/>.</param>
/// <param name="Who">The member it is about, when there is one.</param>
/// <param name="Detail">A title, a mode, a policy or a playback state.</param>
/// <param name="ContentId">The title it is about, for entries that show it by name.</param>
public sealed record WatchPartyActivityEntry(long Id, DateTimeOffset At, string Kind, string? Who = null, string? Detail = null, string? ContentId = null);

public static class WatchPartyActivityKinds
{
    public const string Joined = "joined";
    public const string Left = "left";
    public const string Ready = "ready";
    public const string Unready = "unready";
    public const string Staged = "staged";
    public const string Unstaged = "unstaged";
    public const string Started = "started";
    public const string Stopped = "stopped";
    public const string Mode = "mode";
    public const string Policy = "policy";
    public const string Playback = "playback";
    public const string Suggested = "suggested";
    public const string Unsuggested = "unsuggested";
}

/// <summary>
/// The room's activity as the web derives it (<c>useActivityFeed.ts</c>): the server keeps no log, so every entry is a
/// change between two room snapshots, or two suggestion lists, this client saw while connected. The first observation
/// of a connection is the baseline and says nothing, so coming back does not announce everyone joining. Newest first,
/// at most <see cref="MaxEntries"/>.
/// </summary>
public sealed class WatchPartyActivityFeed
{
    public const int MaxEntries = 50;

    private readonly List<WatchPartyActivityEntry> _entries = [];
    private string? _roomId;
    private bool _connected;
    private WatchPartyRoom? _room;
    private IReadOnlyList<WatchPartySuggestion>? _suggestions;
    private long _nextId;

    public IReadOnlyList<WatchPartyActivityEntry> Entries => _entries;

    /// <summary>Takes one observation; true when it added entries or started a new room's feed.</summary>
    public bool Observe(WatchPartyRoom? room, IReadOnlyList<WatchPartySuggestion> suggestions, bool connected, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        if (room is null)
        {
            return false;
        }
        bool changed = false;
        if (_roomId != room.RoomId)
        {
            changed = _entries.Count > 0;
            _entries.Clear();
            _roomId = room.RoomId;
            _connected = false;
            _room = null;
            _suggestions = null;
        }
        // Before the socket is up observations only seed the baseline; the first connected one is the baseline itself.
        if (!connected || !_connected)
        {
            _connected = connected;
            _room = room;
            _suggestions = suggestions;
            return changed;
        }
        var added = new List<WatchPartyActivityEntry>();
        if (_room is { } before && !ReferenceEquals(before, room))
        {
            added.AddRange(DiffRooms(before, room, at));
        }
        if (_suggestions is { } earlier && !ReferenceEquals(earlier, suggestions))
        {
            added.AddRange(DiffSuggestions(room, earlier, suggestions, at));
        }
        _room = room;
        _suggestions = suggestions;
        if (added.Count == 0)
        {
            return changed;
        }
        added.Reverse();
        _entries.InsertRange(0, added);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        return true;
    }

    /// <summary>A list read whole (a connection's first read) is the baseline: what it holds is not news.</summary>
    public void SeedSuggestions(IReadOnlyList<WatchPartySuggestion> suggestions) => _suggestions = suggestions;

    private IEnumerable<WatchPartyActivityEntry> DiffRooms(WatchPartyRoom before, WatchPartyRoom now, DateTimeOffset at)
    {
        var earlier = before.Members.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var current = now.Members.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, member) in current)
        {
            if (!earlier.TryGetValue(key, out var was))
            {
                yield return Entry(at, WatchPartyActivityKinds.Joined, member.DisplayName);
                continue;
            }
            if (!was.LobbyReady && member.LobbyReady)
            {
                yield return Entry(at, WatchPartyActivityKinds.Ready, member.DisplayName);
            }
            if (was.LobbyReady && !member.LobbyReady && now.IsLobby)
            {
                yield return Entry(at, WatchPartyActivityKinds.Unready, member.DisplayName);
            }
        }
        foreach (var (key, member) in earlier)
        {
            if (!current.ContainsKey(key))
            {
                yield return Entry(at, WatchPartyActivityKinds.Left, member.DisplayName);
            }
        }
        string? host = now.Host?.DisplayName;
        if (before.SelectionMode != now.SelectionMode)
        {
            yield return Entry(at, WatchPartyActivityKinds.Mode, host, now.SelectionMode);
        }
        if (before.GuestControlPolicy != now.GuestControlPolicy)
        {
            yield return Entry(at, WatchPartyActivityKinds.Policy, host, now.GuestControlPolicy);
        }
        if (before.IsLobby && now.IsPlaying)
        {
            yield return Entry(at, WatchPartyActivityKinds.Started, host, contentId: now.SelectedContentId);
        }
        else if (before.IsPlaying && now.IsLobby)
        {
            yield return Entry(at, WatchPartyActivityKinds.Stopped, host, contentId: before.SelectedContentId);
        }
        else if (now.IsLobby && before.SelectedContentId != now.SelectedContentId)
        {
            if (now.SelectedContentId is { Length: > 0 } staged)
            {
                yield return Entry(at, WatchPartyActivityKinds.Staged, host, contentId: staged);
            }
            else if (before.SelectionMode == now.SelectionMode)
            {
                yield return Entry(at, WatchPartyActivityKinds.Unstaged, host);
            }
        }
        if (now.IsPlaying && before.IsPlaying && before.PlaybackState != now.PlaybackState
            && now.PlaybackState is WatchPartyPlaybackStates.Playing or WatchPartyPlaybackStates.Paused)
        {
            yield return Entry(at, WatchPartyActivityKinds.Playback, detail: now.PlaybackState);
        }
    }

    private IEnumerable<WatchPartyActivityEntry> DiffSuggestions(WatchPartyRoom room, IReadOnlyList<WatchPartySuggestion> before,
        IReadOnlyList<WatchPartySuggestion> now, DateTimeOffset at)
    {
        var earlier = before.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var current = now.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var suggestion in now.Where(s => !earlier.Contains(s.Id)))
        {
            yield return Entry(at, WatchPartyActivityKinds.Suggested, Suggester(room, suggestion), suggestion.Title, suggestion.ContentId);
        }
        foreach (var suggestion in before.Where(s => !current.Contains(s.Id)))
        {
            yield return Entry(at, WatchPartyActivityKinds.Unsuggested, Suggester(room, suggestion), suggestion.Title, suggestion.ContentId);
        }
    }

    private static string Suggester(WatchPartyRoom room, WatchPartySuggestion suggestion) =>
        room.Members.FirstOrDefault(m => m.Key == suggestion.SuggesterKey)?.DisplayName ?? "Someone";

    private WatchPartyActivityEntry Entry(DateTimeOffset at, string kind, string? who = null, string? detail = null, string? contentId = null) =>
        new(++_nextId, at, kind, who, detail, contentId);
}
