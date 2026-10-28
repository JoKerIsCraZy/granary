using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

// What members put forward and vote on, as the web's room does (useWatchTogetherRoomConnection.ts): read when a
// connection opens, kept current by suggestions_update (whose rows never say this profile's own vote, so it is kept
// here), and read again after each of this profile's own changes. The host switches the room between picking and
// voting, and plays a suggestion for everyone. The picker's rows and the members' watch state go with the room proof.
public sealed partial class WatchPartySession
{
    /// <summary>The room's suggestions, most votes first, then the oldest.</summary>
    public IReadOnlyList<WatchPartySuggestion> Suggestions
    {
        get;
        private set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                ObserveActivity();
            }
        }
    } = [];

    // Counts the broadcasts: a list read over HTTP before the latest one is older than what shows.
    private long _suggestionsStamp;

    /// <summary>What happened in the room while this device watched it, newest first.</summary>
    public IReadOnlyList<WatchPartyActivityEntry> Activity => _activity.Entries;

    /// <summary>Puts a movie or an episode forward; the list comes back with it.</summary>
    public Task SuggestAsync(WatchPartySuggestionCreate suggestion) =>
        MemberAsync((server, roomId, token) => _api.SuggestAsync(server, roomId, token, suggestion));

    /// <summary>This profile's vote for <paramref name="suggestionId"/>, on or off.</summary>
    public Task SetVoteAsync(string suggestionId, bool vote) =>
        MemberAsync((server, roomId, token) => _api.SetVoteAsync(server, roomId, token, suggestionId, vote));

    /// <summary>Takes a suggestion back (its suggester's own, or any as the host).</summary>
    public Task RemoveSuggestionAsync(string suggestionId) =>
        MemberAsync((server, roomId, token) => _api.DeleteSuggestionAsync(server, roomId, token, suggestionId));

    /// <summary>The host plays a suggestion for everyone.</summary>
    public Task PromoteAsync(string suggestionId) =>
        HostAsync((server, roomId) => _api.PromoteAsync(server, roomId, _roomToken ?? throw NotInRoom(), suggestionId));

    /// <summary>The host switches the lobby to host picks or voting (<see cref="WatchPartySelectionModes"/>).</summary>
    public Task SetSelectionModeAsync(string mode) => HostAsync((server, roomId) => _api.SetSelectionModeAsync(server, roomId, mode));

    /// <summary>The picker's rows over the room's members; null while in no room.</summary>
    public async Task<WatchPartyPicker?> GetPickerAsync(CancellationToken cancellationToken = default) =>
        Room is { } room && _roomToken is { } token ? await _api.GetPickerAsync(Server(), room.RoomId, token, cancellationToken) : null;

    /// <summary>What each member has watched of <paramref name="contentIds"/>; null while in no room.</summary>
    public async Task<WatchPartyMemberStates?> GetMemberStatesAsync(IReadOnlyList<string> contentIds, CancellationToken cancellationToken = default) =>
        Room is { } room && _roomToken is { } token ? await _api.GetMemberStatesAsync(Server(), room.RoomId, token, contentIds, cancellationToken) : null;

    private static InvalidOperationException NotInRoom() => new("This profile is in no Watch Party.");

    // A member's own change, then the list as it is after it. A clear refusal (4xx) changed nothing; any other failure
    // may have gone through, so the list is read back before the failure is told, as the server's docs ask.
    private async Task MemberAsync(Func<Uri, string, string, Task> change)
    {
        if (Room is not { } room || _roomToken is not { } token)
        {
            return;
        }
        long generation = _generation;
        long stamp = _suggestionsStamp;
        var server = Server();
        bool reread = true;
        try
        {
            await change(server, room.RoomId, token);
        }
        catch (SiloApiException ex) when (ex.Status is >= 400 and < 500)
        {
            reread = false;
            throw;
        }
        finally
        {
            if (reread)
            {
                await ReadBackAsync(server, room.RoomId, token, generation, stamp);
            }
        }
    }

    // The list after a change of this profile's; a failed read is logged, the broadcast brings the list anyway.
    private async Task ReadBackAsync(Uri server, string roomId, string token, long generation, long stamp)
    {
        try
        {
            TakeRead(await _api.ListSuggestionsAsync(server, roomId, token), generation, stamp);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The room's suggestions could not be read back: {Failure}", ex.GetType().Name);
        }
    }

    // A read whole: dropped for a room left meanwhile. A broadcast since it was asked for is newer: of the read, only
    // this profile's own votes count then.
    private void TakeRead(IReadOnlyList<WatchPartySuggestion> read, long generation, long stamp)
    {
        if (generation != _generation || _connection is null)
        {
            return;
        }
        if (stamp == _suggestionsStamp)
        {
            Suggestions = read;
            return;
        }
        var mine = read.ToDictionary(s => s.Id, s => s.VotedByMe, StringComparer.Ordinal);
        Suggestions = [.. Suggestions.Select(s => mine.TryGetValue(s.Id, out bool voted) && voted != s.VotedByMe ? s with { VotedByMe = voted } : s)];
    }

    // Each connection reads the list once: suggestions_update tells only of changes from then on.
    private async Task LoadSuggestionsAsync()
    {
        if (Room is not { } room || _roomToken is not { } token)
        {
            return;
        }
        long generation = _generation;
        long stamp = _suggestionsStamp;
        try
        {
            var suggestions = await _api.ListSuggestionsAsync(Server(), room.RoomId, token);
            if (generation == _generation && _connection is not null && stamp == _suggestionsStamp)
            {
                _activity.SeedSuggestions(suggestions);
            }
            TakeRead(suggestions, generation, stamp);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The room's suggestions could not be read: {Failure}", ex.GetType().Name);
        }
    }

    // The broadcast's rows carry no one's own vote: this profile's are kept from what it knew.
    private void TakeSuggestions(IReadOnlyList<WatchPartySuggestion> broadcast)
    {
        _suggestionsStamp++;
        var mine = Suggestions.Where(s => s.VotedByMe).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Suggestions = WatchPartyApi.Rank(broadcast.Select(s => s with { VotedByMe = mine.Contains(s.Id) }));
    }

    private void ObserveActivity()
    {
        if (_activity.Observe(Room, Suggestions, IsConnected, _time.GetUtcNow()))
        {
            OnPropertyChanged(nameof(Activity));
        }
    }
}
