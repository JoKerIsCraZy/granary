using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;

namespace Granary.Tests.WatchParty;

/// <summary>The room's activity, as the web derives it from what changed between two snapshots or suggestion lists.</summary>
public sealed class WatchPartyActivityFeedTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static WatchPartyMember Member(string user, string name, bool host = false, bool ready = false) =>
        new() { UserId = user, ProfileId = "p" + user, DisplayName = name, IsHost = host, LobbyReady = ready, Connected = true };

    private static WatchPartyRoom Room(params WatchPartyMember[] members) => new()
    {
        RoomId = "r1",
        Phase = WatchPartyPhases.Lobby,
        PlaybackState = WatchPartyPlaybackStates.Idle,
        SelectionMode = WatchPartySelectionModes.HostPick,
        GuestControlPolicy = WatchPartyPolicies.HostOnly,
        Members = members,
    };

    private static WatchPartySuggestion Suggestion(string id, string title, string user = "2") =>
        new() { Id = id, Title = title, SuggesterUserId = user, SuggesterProfileId = "p" + user, ContentId = "movie:" + id };

    private readonly WatchPartyActivityFeed _feed = new();

    [Fact]
    public void TheFirstConnectedObservation_IsTheBaseline()
    {
        var host = Member("1", "Elena", host: true);
        Assert.False(_feed.Observe(Room(host), [], connected: false, At));
        Assert.False(_feed.Observe(Room(host, Member("2", "Maya")), [], connected: true, At));

        Assert.Empty(_feed.Entries);
    }

    [Fact]
    public void Members_JoinLeaveAndGetReady_NewestFirst()
    {
        var host = Member("1", "Elena", host: true);
        _feed.Observe(Room(host, Member("2", "Maya")), [], connected: true, At);
        _feed.Observe(Room(host, Member("2", "Maya")), [], connected: true, At);

        Assert.True(_feed.Observe(Room(host, Member("2", "Maya", ready: true), Member("3", "Noor")), [], connected: true, At.AddSeconds(5)));
        Assert.True(_feed.Observe(Room(host, Member("3", "Noor")), [], connected: true, At.AddSeconds(9)));

        Assert.Equal([("left", "Maya"), ("joined", "Noor"), ("ready", "Maya")], _feed.Entries.Select(e => (e.Kind, e.Who)));
    }

    [Fact]
    public void TheHostsMoves_ModePolicyStagingAndPlayback_AreTheHosts()
    {
        var host = Member("1", "Elena", host: true);
        var lobby = Room(host);
        _feed.Observe(lobby, [], connected: true, At);
        _feed.Observe(lobby, [], connected: true, At);

        var voting = lobby with { SelectionMode = WatchPartySelectionModes.Vote, GuestControlPolicy = WatchPartyPolicies.GuestPlayPause };
        _feed.Observe(voting, [], connected: true, At);
        var staged = voting with { SelectedContentId = "movie:heat" };
        _feed.Observe(staged, [], connected: true, At);
        var playing = staged with { Phase = WatchPartyPhases.Playing, PlaybackState = WatchPartyPlaybackStates.Playing };
        _feed.Observe(playing, [], connected: true, At);
        _feed.Observe(playing with { PlaybackState = WatchPartyPlaybackStates.Paused }, [], connected: true, At);
        _feed.Observe(staged, [], connected: true, At);

        Assert.Equal(["stopped", "playback", "started", "staged", "policy", "mode"], _feed.Entries.Select(e => e.Kind));
        Assert.All(_feed.Entries.Where(e => e.Kind != "playback"), e => Assert.Equal("Elena", e.Who));
        Assert.Equal("movie:heat", _feed.Entries[0].ContentId);
        Assert.Equal("paused", _feed.Entries[1].Detail);
    }

    [Fact]
    public void Suggestions_ComeAndGo_ByTheirSuggester()
    {
        var room = Room(Member("1", "Elena", host: true), Member("2", "Maya"));
        _feed.Observe(room, [], connected: true, At);
        _feed.Observe(room, [], connected: true, At);

        _feed.Observe(room, [Suggestion("s1", "Heat")], connected: true, At);
        _feed.Observe(room, [Suggestion("s2", "Alien", user: "9")], connected: true, At);

        Assert.Equal([("unsuggested", "Maya", "Heat"), ("suggested", "Someone", "Alien"), ("suggested", "Maya", "Heat")],
            _feed.Entries.Select(e => (e.Kind, e.Who, e.Detail)));
    }

    [Fact]
    public void AnotherRoom_StartsAnew_AndTheFeedKeepsFifty()
    {
        var room = Room(Member("1", "Elena", host: true));
        _feed.Observe(room, [], connected: true, At);
        _feed.Observe(room, [], connected: true, At);
        IReadOnlyList<WatchPartySuggestion> last = [];
        for (int i = 0; i < 60; i++)
        {
            last = [Suggestion("s" + i, "Title " + i)];
            _feed.Observe(room, last, connected: true, At);
        }
        Assert.Equal(WatchPartyActivityFeed.MaxEntries, _feed.Entries.Count);

        Assert.True(_feed.Observe(room with { RoomId = "r2" }, [], connected: true, At));
        Assert.Empty(_feed.Entries);
    }
}
