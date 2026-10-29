using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The room page as the web's: the status strip, the vote and its suggestions, the shelf, the candidate and the rail.</summary>
public sealed class WatchPartyRoomStageTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private const string HomeSections = """
        { "sections": [
          { "id": "cw", "section_type": "continue_watching", "title": "Continue Watching", "item_limit": 20, "total_count": 1,
            "items": [ { "content_id": "mine", "type": "movie", "title": "Mine Only" } ] },
          { "id": "trending", "section_type": "trending", "title": "Trending this week", "item_limit": 20, "total_count": 3,
            "items": [ { "content_id": "evan", "type": "movie", "title": "Evan Allmächtig", "year": 2007 },
                       { "content_id": "dark", "type": "series", "title": "Dark", "year": 2017 },
                       { "content_id": "ep", "type": "episode", "title": "An Episode" } ] } ] }
        """;

    private const string Recent = """
        { "window_cursor": "w", "items": [ { "content_id": "blair", "type": "movie", "title": "Blair Witch Project", "year": 1999 } ],
          "page": { "has_more": false }, "total": 1, "total_exact": true }
        """;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();
    private readonly WatchPartySession _party;
    private readonly WatchPartyApi _api;

    public WatchPartyRoomStageTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Get, "api/v2/watch-together/capabilities", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "lobby_ready": true, "stop_playback": true }"""));
        _silo.On(HttpMethod.Get, "api/v2/images/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities));
        _silo.On(HttpMethod.Get, Room + "/picker", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Picker));
        _silo.On(HttpMethod.Get, "api/v2/home/sections", ScriptedSilo.Json(HttpStatusCode.OK, HomeSections));
        _silo.On(HttpMethod.Get, "api/v2/catalog", ScriptedSilo.Json(HttpStatusCode.OK, Recent));
        _silo.On(HttpMethod.Get, "api/v2/catalog/items/heat", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "content_id": "heat", "type": "movie", "title": "Heat", "year": 1995, "runtime": 170, "content_rating": "R", "genres": ["Crime", "Drama", "Thriller"] }
            """));
        Suggestions();
        _api = new WatchPartyApi(_silo.Http);
        _party = new WatchPartySession(_session, _api, () => new WatchPartyConnection(_api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>(), time: _time);
    }

    private void Suggestions(params string[] rows) =>
        _silo.On(HttpMethod.Get, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Suggestions(rows)));

    private WatchPartyRoomViewModel Page() => new(_party, new WatchPartyFeature(_session, _api, _time, _logs.For<WatchPartyFeature>()), _session,
        new CatalogApi(_silo.Http), new ImageParameterCache(new ImagesApi(_silo.Http), _logs.For<ImageParameterCache>()), _navigation,
        new ImmediateDispatcher(), _time, _logs.For<WatchPartyRoomViewModel>());

    private async Task<WatchPartyRoomViewModel> InRoomAsync(string room, int suggestions = 0)
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected && _party.Suggestions.Count == suggestions, "the room");
        var page = Page();
        page.Activate();
        return page;
    }

    [Fact]
    public async Task TheStatusStrip_SaysWhatTheRoomDoes_AndHowItPicks()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room());

        Assert.Equal(("Live · 2 here", "Host picks", true, "Connected"), (page.PhaseLabel, page.ModeLabel, page.CanSwitchMode, page.ConnectionLabel));
        Assert.True(page.ShowsEmptyLobby);
        Assert.Equal("Pick something below and it lands here for everyone to see. Or let the room decide.", page.LobbyText);

        _sockets[0].Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 5, revision: 1, selected: "heat")));
        await ServerPlaybackFixture.Until(() => page.IsPlaying, "the start");

        Assert.Equal(("Playing · 2 here", false), (page.PhaseLabel, page.CanSwitchMode));
        page.Deactivate();
    }

    [Fact]
    public async Task OpeningTheFloor_SwitchesTheRoomToVotes()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room());
        _silo.On(HttpMethod.Patch, Room + "/selection-mode",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 2, mode: "vote"))));

        await page.OpenVotingCommand.ExecuteAsync(null);

        Assert.Equal("""{"selection_mode":"vote"}""", Assert.Single(_silo.SentTo(HttpMethod.Patch, Room + "/selection-mode")).Body);
        Assert.Equal(("Voting · 2 here", "Everyone votes"), (page.PhaseLabel, page.ModeLabel));
        Assert.True(page.ShowsVoting);
        Assert.True(page.HasNoLeader);
        Assert.Equal("Anyone can add one from the shelf below. The room votes, and you start whatever wins.", page.VotingEmptyText);
        page.Deactivate();
    }

    [Fact]
    public async Task TheVote_RanksTheSuggestions_AndTheHostPlaysTheLeader()
    {
        Suggestions(WatchPartyPayloads.Suggestion("s2", "Alien", content: "alien", created: "2026-10-01T11:00:00Z"),
            WatchPartyPayloads.Suggestion("s1", "Heat", votes: 2, mine: true, content: "heat"));
        var page = await InRoomAsync(WatchPartyPayloads.Room(mode: "vote"), suggestions: 2);
        await ServerPlaybackFixture.Until(() => page.HeroMeta.Length > 0, "the leader's facts");

        Assert.Equal(("Leading · 2 of 2 votes", "Heat (1995)", "2h 50m · R · Crime, Drama"), (page.LeaderEyebrow, page.HeroHeadline, page.HeroMeta));
        Assert.Equal(("Play Heat for everyone", "Start the leader, or play any suggestion below"), (page.PlayLeaderLabel, page.LeaderCaption));
        Assert.Equal(("Suggestions · ranked by votes", "One vote each · change it any time"), (page.SuggestionsHeading, page.SuggestionsHint));
        Assert.Equal([("Heat", "2 votes", true, 1.0, "Maya"), ("Alien", "0 votes", false, 0.0, "Maya")],
            page.SuggestionRows.Select(r => (r.Title, r.VotesText, r.IsLeading, r.Share, r.SuggesterName)));
        Assert.All(page.SuggestionRows, r => Assert.Equal((true, true, "Play this"), (r.CanVote, r.CanPromote, r.PromoteLabel)));
        _silo.On(HttpMethod.Post, Room + "/suggestions/promote", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room("playing", "waiting", generation: 3, revision: 1, selected: "heat", mode: "vote"))));

        await page.PlayLeaderCommand.ExecuteAsync(null);

        Assert.Equal("""{"suggestion_id":"s1"}""", Assert.Single(_silo.SentTo(HttpMethod.Post, Room + "/suggestions/promote")).Body);
        Assert.Equal(AppRoute.Player, _navigation.Navigations[^1].Route);
        page.Deactivate();
    }

    [Fact]
    public async Task AGuest_Votes_AndTakesBackOnlyTheirOwn()
    {
        Suggestions(WatchPartyPayloads.Suggestion("s1", "Heat", content: "heat"),
            WatchPartyPayloads.Suggestion("s2", "Alien", user: "1", profile: "7", content: "alien", created: "2026-10-01T12:30:00Z"));
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"), suggestions: 2);
        _silo.On(HttpMethod.Post, Room + "/suggestions/s1/vote", ScriptedSilo.NoContent());

        Assert.Equal([false, true], page.SuggestionRows.Select(r => r.CanDelete));
        Assert.All(page.SuggestionRows, r => Assert.False(r.CanPromote));
        Assert.Equal("Starts when Elena presses play", page.LeaderCaption);
        Assert.Equal("First suggestion", page.LeaderEyebrow);
        await page.VoteCommand.ExecuteAsync(page.SuggestionRows[0]);

        Assert.Single(_silo.SentTo(HttpMethod.Post, Room + "/suggestions/s1/vote"));
        page.Deactivate();
    }

    [Fact]
    public async Task InAHostPickLobby_TheHostQueuesAGuestsSuggestion()
    {
        Suggestions(WatchPartyPayloads.Suggestion("s1", "Heat", content: "heat"));
        var page = await InRoomAsync(WatchPartyPayloads.Room(), suggestions: 1);
        _silo.On(HttpMethod.Put, Room + "/staged-selection", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 2, selected: "heat"))));

        Assert.True(page.ShowsSuggestions);
        Assert.Equal(("Suggestions · 1", "Queue one to put it up next."), (page.SuggestionsHeading, page.SuggestionsHint));
        var row = Assert.Single(page.SuggestionRows);
        Assert.Equal((false, "Queue this"), (row.ShowsVotes, row.PromoteLabel));
        await page.PromoteCommand.ExecuteAsync(row);

        Assert.Equal("""{"content_id":"heat"}""", Assert.Single(_silo.SentTo(HttpMethod.Put, Room + "/staged-selection")).Body);
        Assert.True(page.ShowsStaged);
        Assert.True(page.ShowsInvite);
        page.Deactivate();
    }

    [Fact]
    public async Task TheShelf_ShowsWhatTheRoomHasInCommon_WithWhoItIsAbout()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest"));
        await ServerPlaybackFixture.Until(() => page.Shelf.Rows.Count == 5, "the shelf");

        Assert.Equal(["Continue watching together", "On your watchlists", "Trending this week", "Recently added movies", "Recently added series"],
            page.Shelf.Rows.Select(r => r.Title));
        var dark = Assert.Single(page.Shelf.Rows[0].Cards);
        Assert.Equal(("S2 E3", 0.2, 2), (dark.Caption, dark.Progress!.Value, dark.Season));
        Assert.Equal([("EL", 0), ("MA", 1)], dark.Badges.Select(b => (b.Initials, b.Tint)));
        var heat = Assert.Single(page.Shelf.Rows[1].Cards);
        Assert.Equal(("anyone in the room", 1, "Maya, Elena, Noor"), (page.Shelf.Rows[1].Hint, heat.MoreMembers, heat.MemberNames));
        Assert.Equal(["Evan Allmächtig", "Dark"], page.Shelf.Rows[2].Cards.Select(c => c.Title));
        Assert.False(page.Shelf.Picks);

        page.Shelf.SelectedChip = page.Shelf.Chips.Single(c => c.Value == WatchPartyShelfViewModel.WatchlistsChip);
        Assert.Equal(["On your watchlists"], page.Shelf.Rows.Select(r => r.Title));
        page.Shelf.SelectedChip = page.Shelf.Chips.Single(c => c.Value == WatchPartyShelfViewModel.MoviesChip);
        Assert.Equal(["Trending this week", "Recently added movies"], page.Shelf.Rows.Select(r => r.Title));
        Assert.Equal(["Evan Allmächtig"], page.Shelf.Rows[0].Cards.Select(c => c.Title));
        page.Deactivate();
    }

    [Fact]
    public async Task ACardOnTheStage_IsSuggested_AndTheRoomSaysSo()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        await ServerPlaybackFixture.Until(() => page.Shelf.Rows.Count > 0, "the shelf");
        _silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.Created, """{ "suggestion_id": "s5" }"""));
        Suggestions(WatchPartyPayloads.Suggestion("s5", "Blair Witch Project", user: "1", profile: "7", content: "blair", subtitle: "1999"));
        var card = page.Shelf.Rows.Single(r => r.Title == "Recently added movies").Cards[0];

        var rows = page.Shelf.Rows.ToList();

        page.Shelf.ChooseCommand.Execute(card);
        Assert.True(page.HasCandidate);
        Assert.False(page.ShowsVoting);
        // Only the ring moves: the rows the viewer scrolled stay as they are.
        Assert.True(card.IsSelected);
        Assert.Equal(rows, page.Shelf.Rows);
        Assert.Equal(("Suggest this", "Blair Witch Project (1999)"), (page.Candidate.ConfirmLabel, page.Candidate.Headline));
        page.Candidate.ConfirmCommand.Execute(null);
        await ServerPlaybackFixture.Until(() => !page.HasCandidate, "the suggestion");

        using var body = JsonDocument.Parse(Assert.Single(_silo.SentTo(HttpMethod.Post, Room + "/suggestions")).Body!);
        Assert.Equal(("blair", "movie", "Blair Witch Project", "1999"), (body.RootElement.GetProperty("content_id").GetString(),
            body.RootElement.GetProperty("content_type").GetString(), body.RootElement.GetProperty("title").GetString(),
            body.RootElement.GetProperty("subtitle").GetString()));
        Assert.Equal("Suggested Blair Witch Project", page.Message);
        Assert.Equal(["Blair Witch Project"], page.SuggestionRows.Select(r => r.Title));
        _time.Advance(WatchPartyRoomViewModel.MessageFor);
        Assert.Equal("", page.Message);
        page.Deactivate();
    }

    [Fact]
    public async Task ASuggestionRetriedAfterALostAnswer_IsTheSameSuggestion()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"));
        await ServerPlaybackFixture.Until(() => page.Shelf.Rows.Count > 0, "the shelf");
        _silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.Created, """{ "suggestion_id": "s5" }"""));
        page.Shelf.ChooseCommand.Execute(page.Shelf.Rows.Single(r => r.Title == "Recently added movies").Cards[0]);

        await page.ConfirmAsync(new WatchPartyChoice("blair", "movie", "Blair Witch Project", "1999", null));
        Assert.True(page.HasCandidate);
        await page.ConfirmAsync(new WatchPartyChoice("blair", "movie", "Blair Witch Project", "1999", null));

        var ids = _silo.SentTo(HttpMethod.Post, Room + "/suggestions").Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("suggestion_id").GetString()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
        Assert.False(page.HasCandidate);
        page.Deactivate();
    }

    [Fact]
    public async Task AVote_ChangesItsRowInPlace()
    {
        Suggestions(WatchPartyPayloads.Suggestion("s1", "Heat", content: "heat"), WatchPartyPayloads.Suggestion("s2", "Alien", content: "alien",
            created: "2026-10-01T12:30:00Z"));
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest", mode: "vote"), suggestions: 2);
        var heat = page.SuggestionRows[0];
        var alien = page.SuggestionRows[1];

        _sockets[0].Push(WatchPartyPayloads.SuggestionsUpdate(("s2", "Alien", 3), ("s1", "Heat", 1)));
        // The whole update, not only the move: the rows are reordered before their votes are set, and this polls from
        // another thread.
        await ServerPlaybackFixture.Until(() => page.SuggestionRows[0].Title == "Alien" && alien.IsLeading, "the new order and its votes");

        Assert.Same(alien, page.SuggestionRows[0]);
        Assert.Same(heat, page.SuggestionRows[1]);
        Assert.Equal(("3 votes", true), (alien.VotesText, alien.IsLeading));
        page.Deactivate();
    }

    [Fact]
    public async Task TheHostOfAHostPickLobby_StagesTheCandidate()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room());
        await ServerPlaybackFixture.Until(() => page.Shelf.Rows.Count > 0, "the shelf");
        _silo.On(HttpMethod.Put, Room + "/staged-selection", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 2, selected: "blair"))));
        Assert.True(page.Shelf.Picks);

        page.Shelf.ChooseCommand.Execute(page.Shelf.Rows.Single(r => r.Title == "Recently added movies").Cards[0]);
        Assert.Equal("Stage it for the room", page.Candidate.ConfirmLabel);
        page.Candidate.ConfirmCommand.Execute(null);
        await ServerPlaybackFixture.Until(() => page.ShowsStaged, "the staged title");

        Assert.Equal("""{"content_id":"blair"}""", Assert.Single(_silo.SentTo(HttpMethod.Put, Room + "/staged-selection")).Body);
        Assert.True(page.Shelf.IsCollapsible);
        Assert.Equal("Change what's up next", page.Shelf.FoldedLabel);
        page.Deactivate();
    }

    [Fact]
    public async Task TheInviteRow_AndTheRail_ReadAsTheWebs()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room(selected: "heat"));

        Assert.Equal("nas.example/silo/rooms/join?token=invite-secret", page.InviteText);
        Assert.Equal([("Elena", "EL", true, true, "Connected", 0), ("Maya", "MA", false, false, "Ready", 1)],
            page.Members.Select(m => (m.Name, m.Initials, m.IsHost, m.IsSelf, m.State, m.Tint)));
        Assert.True(page.Members[1].ShowsReadyTick);
        Assert.Equal((2, "If you drop, the room waits 2 minutes for you before it ends."), (page.PeopleCount, page.RailFooter));

        _sockets[0].Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 4, selected: "heat", policy: "guest_play_pause", mayaReady: false)));
        await ServerPlaybackFixture.Until(() => page.ActivityRows.Count == 2, "the activity");

        Assert.Equal(["Elena set guests can pause", "Maya is no longer ready"], page.ActivityRows.Select(r => r.Text));
        Assert.All(page.ActivityRows, r => Assert.Equal("now", r.Time));
        page.Deactivate();
    }

    [Fact]
    public async Task AGuestsRail_NamesTheHostAndWhoControlsPlayback()
    {
        var page = await InRoomAsync(WatchPartyPayloads.Room(role: "guest"));

        Assert.Equal("Host: Elena · host controls playback", page.RailFooter);
        Assert.Equal("Elena is deciding. Anything you pick below goes in as a suggestion.", page.LobbyText);
        Assert.False(page.CanSwitchMode);
        Assert.True(page.ShowsModeLabel);
        page.Deactivate();
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
