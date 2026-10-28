using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>Suggestions, votes, the host's mode and promotion, the picker and the members' watch state, as the server takes them.</summary>
public sealed class WatchPartySuggestionsApiTests
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();
    private readonly WatchPartyApi _api;

    public WatchPartySuggestionsApiTests() => _api = new WatchPartyApi(_silo.Http);

    [Fact]
    public async Task TheSuggestions_AreReadPageByPage_WithTheProof_AndRanked()
    {
        _silo.On(HttpMethod.Get, Room + "/suggestions",
            ScriptedSilo.Json(HttpStatusCode.OK, $$"""
                { "items": [{{WatchPartyPayloads.Suggestion("s1", "Heat", votes: 1, created: "2026-10-01T12:00:00Z")}}],
                  "page": { "has_more": true, "next_cursor": "c2" } }
                """),
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Suggestions(
                WatchPartyPayloads.Suggestion("s2", "Alien", votes: 2, mine: true, created: "2026-10-01T12:05:00Z"),
                WatchPartyPayloads.Suggestion("s3", "Ronin", votes: 1, created: "2026-09-30T12:00:00Z"))));

        var suggestions = await _api.ListSuggestionsAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Alien", "Ronin", "Heat"], suggestions.Select(s => s.Title));
        Assert.True(suggestions[0].VotedByMe);
        Assert.Equal("2:9", suggestions[0].SuggesterKey);
        Assert.All(_silo.Sent, r => Assert.Equal(WatchPartyPayloads.Token, r.RoomToken));
        Assert.Contains("limit=100", _silo.Sent[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("cursor=c2", _silo.Sent[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuggestion_GoesWithItsOwnId_AndAVote_OnOrOff()
    {
        _silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.Created, """{ "suggestion_id": "s9" }"""));
        _silo.On(HttpMethod.Post, Room + "/suggestions/s9/vote", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, Room + "/suggestions/s9/vote", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, Room + "/suggestions/s9", ScriptedSilo.NoContent());
        var ct = TestContext.Current.CancellationToken;

        var receipt = await _api.SuggestAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token,
            new WatchPartySuggestionCreate("0e2b5f1a-0000-4000-8000-000000000001", "movie:heat", "movie", "Heat", "1995", "/img/heat.jpg"), ct);
        await _api.SetVoteAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, "s9", vote: true, ct);
        await _api.SetVoteAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, "s9", vote: false, ct);
        await _api.DeleteSuggestionAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, "s9", ct);

        Assert.Equal("s9", receipt.SuggestionId);
        Assert.Equal("""{"suggestion_id":"0e2b5f1a-0000-4000-8000-000000000001","content_id":"movie:heat","content_type":"movie","title":"Heat","subtitle":"1995","poster_url":"/img/heat.jpg"}""",
            _silo.Sent[0].Body);
        Assert.Equal([HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Delete], _silo.Sent.Select(r => r.Method));
        Assert.All(_silo.Sent, r => Assert.Equal(WatchPartyPayloads.Token, r.RoomToken));
    }

    [Fact]
    public async Task TheHost_PromotesWithTheProof_AndSwitchesTheModeByProfile()
    {
        _silo.On(HttpMethod.Post, Room + "/suggestions/promote",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room("playing", "waiting", generation: 3, revision: 1, selected: "movie:heat", mode: "vote"))));
        _silo.On(HttpMethod.Patch, Room + "/selection-mode",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(mode: "vote"))));
        var ct = TestContext.Current.CancellationToken;

        var promoted = await _api.PromoteAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, "s1", ct);
        var switched = await _api.SetSelectionModeAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartySelectionModes.Vote, ct);

        Assert.True(promoted.Room.IsPlaying);
        Assert.Equal("""{"suggestion_id":"s1"}""", _silo.Sent[0].Body);
        Assert.Equal(WatchPartyPayloads.Token, _silo.Sent[0].RoomToken);
        Assert.Equal("vote", switched.Room.SelectionMode);
        Assert.Equal("""{"selection_mode":"vote"}""", _silo.Sent[1].Body);
        Assert.Null(_silo.Sent[1].RoomToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _api.SetSelectionModeAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, "random", ct));
    }

    [Fact]
    public async Task ThePicker_NamesItsMembers_AndASeriesNextEpisode()
    {
        _silo.On(HttpMethod.Get, Room + "/picker", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Picker));

        var picker = await _api.GetPickerAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, TestContext.Current.CancellationToken);

        var together = Assert.Single(picker.ContinueTogether);
        Assert.Equal(("series:dark", "Dark", 2017), (together.Item.ContentId, together.Item.Title, together.Item.Year));
        Assert.Equal(["1:7", "2:9"], together.Members.Select(m => m.Key));
        Assert.Equal((2, 3), (together.NextUp!.SeasonNumber, together.NextUp.EpisodeNumber));
        Assert.Equal(["Maya", "Elena", "Noor"], picker.WatchlistUnion[0].Members.Select(m => m.DisplayName));
        Assert.Equal(WatchPartyPayloads.Token, Assert.Single(_silo.Sent).RoomToken);
    }

    [Fact]
    public async Task TheMembersWatchState_IsAskedFor200TitlesAtMost()
    {
        _silo.On(HttpMethod.Post, Room + "/member-state", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "members": [], "items": [ { "content_id": "movie:heat", "members": [
              { "user_id": 2, "profile_id": "9", "state": "in_progress", "position_seconds": 2700, "duration_seconds": 6000, "on_watchlist": true } ] } ] }
            """));
        var ct = TestContext.Current.CancellationToken;

        var states = await _api.GetMemberStatesAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token, ["movie:heat"], ct);

        var maya = Assert.Single(Assert.Single(states.Items).Members);
        Assert.Equal(("2:9", "in_progress", true), (maya.Key, maya.State, maya.OnWatchlist));
        Assert.Equal("""{"content_ids":["movie:heat"]}""", _silo.Sent[0].Body);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _api.GetMemberStatesAsync(ScriptedSilo.Base, WatchPartyPayloads.RoomId,
            WatchPartyPayloads.Token, [.. Enumerable.Range(0, 201).Select(i => $"movie:{i}")], ct));
    }

    [Fact]
    public void TheSocketsSnapshot_WithNumbersForIds_IsTheRoom()
    {
        string snapshot = WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 4, revision: 1, selected: "movie:heat")
            .Replace("\"selected_file_id\": \"77\"", "\"selected_file_id\": 77", StringComparison.Ordinal)
            .Replace("\"user_id\": \"1\"", "\"user_id\": 1", StringComparison.Ordinal)
            .Replace("\"user_id\": \"2\"", "\"user_id\": 2", StringComparison.Ordinal));

        var room = Assert.IsType<WatchPartyFrame.Snapshot>(WatchPartyFrames.Parse(snapshot)).Room;

        Assert.Equal("77", room.SelectedFileId);
        Assert.Equal(["1:7", "2:9"], room.Members.Select(m => m.Key));
    }

    [Fact]
    public void ASuggestionsUpdate_IsTheList()
    {
        var frame = WatchPartyFrames.Parse(WatchPartyPayloads.SuggestionsUpdate(("s1", "Heat", 2), ("s2", "Alien", 0)));

        var update = Assert.IsType<WatchPartyFrame.Suggestions>(frame);
        Assert.Equal(["Heat", "Alien"], update.Items.Select(s => s.Title));
        Assert.Equal("2:9", update.Items[0].SuggesterKey);
    }
}
