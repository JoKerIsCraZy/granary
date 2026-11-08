using System.Net;
using System.Text.Json.Nodes;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Settings;
using Granary.Core.Api.WatchParty;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>
/// "Start a Watch Party" on a title's page and its sheet, as the web's StartPartySheet.tsx and in its words; what guards it
/// against failures, a changed profile or server, and a dismissed sheet is in StartPartyTests.Guards.cs.
/// </summary>
public sealed partial class StartPartyTests : IAsyncDisposable
{
    private const string Rooms = "api/v2/watch-together/rooms";
    private const string Room = Rooms + "/" + WatchPartyPayloads.RoomId;
    private const string Capabilities = "api/v2/watch-together/capabilities";
    private const string Effective = "api/v2/settings/values/effective";
    private const string Hosting = """{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "staged_selection": true }""";
    private const string Invite = "Invite copied. Room code ABCD1234";

    private readonly ItemDetailFixture _page = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly List<string> _copied = [];
    private readonly WatchPartyApi _api;
    private readonly WatchPartySession _party;
    private readonly WatchPartyFeature _feature;
    private readonly WatchPartyRecentRooms _recent;

    public StartPartyTests()
    {
        _page.Silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK, Hosting));
        _page.Silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _page.Silo.On(HttpMethod.Get, Effective, ScriptedSilo.Json(HttpStatusCode.OK, Saved("host_only")));
        _api = new WatchPartyApi(_page.Silo.Http);
        _recent = new WatchPartyRecentRooms(new InMemorySecretStore(), _page.Clock, _page.Logs.For<WatchPartyRecentRooms>());
        _party = new WatchPartySession(_page.Session, _api, () => new WatchPartyConnection(_api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _page.Logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _page.Logs.For<WatchPartySession>(), _recent);
        _feature = new WatchPartyFeature(_page.Session, _api, _page.Clock, _page.Logs.For<WatchPartyFeature>());
    }

    /// <summary>The room's name among this profile's recent parties; null when nothing named it.</summary>
    private async Task<string?> RecentTitleAsync() =>
        Assert.Single(await _recent.ListAsync(ScriptedSilo.Server.Id, "7", TestContext.Current.CancellationToken)).Title;

    private static string Saved(string policy) =>
        $$"""{ "items": [{ "key": "watch_party.guest_control_policy", "value": "{{policy}}", "source": "profile" }], "revision": 3 }""";

    private static Func<Task<HttpResponseMessage>> Created(string room) => ScriptedSilo.Json(HttpStatusCode.Created, WatchPartyPayloads.Answer(room));

    private static Func<Task<HttpResponseMessage>> Answered(string room) => ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room));

    // The clipboard takes the invite unless a test says it fails.
    private StartPartyViewModel Sheet(INavigationService? navigation = null, bool copies = true) => new(_party, _feature, _page.Session,
        new CatalogApi(_page.Silo.Http), new SettingsApi(_page.Silo.Http), navigation ?? _page.Navigation, _page.Logs.For<StartPartyViewModel>())
    {
        CopyText = text =>
        {
            _copied.Add(text);
            return copies;
        },
    };

    // The page loaded and the server's rooms read, as the sidebar reads them when the profile is picked.
    private async Task<(ItemDetailViewModel Page, StartPartyViewModel Sheet)> AttachAsync(string id, StartPartyViewModel? sheet = null)
    {
        var page = await _page.LoadAsync(id);
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        sheet ??= Sheet();
        sheet.Attach(page);
        return (page, sheet);
    }

    private async Task<StartPartyViewModel> OpenAsync(string id, StartPartyViewModel? sheet = null)
    {
        (_, sheet) = await AttachAsync(id, sheet);
        Assert.True(sheet.Open());
        await sheet.Opening;
        return sheet;
    }

    private WatchPartyArrival? Arrival() =>
        Assert.Single(_page.Navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom).Parameter as WatchPartyArrival;

    [Fact]
    public async Task TheEntry_IsOffered_ForAMovieWithAFile_WhileTheServerHostsRooms()
    {
        _page.Movie();

        var (page, sheet) = await AttachAsync(ItemDetailPayloads.MovieId);

        Assert.True(page.OffersWatchParty);
        Assert.True(sheet.IsOffered);
    }

    [Theory]
    [InlineData("""{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "staged_selection": false }""")]
    [InlineData("""{ "state": "not_configured", "allowed": true, "socket_protocol": "" }""")]
    [InlineData("""{ "state": "available", "allowed": false, "socket_protocol": "silo.room.v2", "staged_selection": true }""")]
    public async Task TheEntry_StaysAway_WhereTheServerHostsNoRoomWithATitleUp(string capabilities)
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK, capabilities));

        var (_, sheet) = await AttachAsync(ItemDetailPayloads.MovieId);

        Assert.False(sheet.IsOffered);
        Assert.False(sheet.Open());
    }

    [Fact]
    public async Task AMovieWithoutAFile_OffersNoParty()
    {
        var movie = JsonNode.Parse(ItemDetailPayloads.Movie)!;
        movie["versions"] = new JsonArray();
        _page.Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.MovieId), ItemDetailFixture.Ok(movie.ToJsonString()))
            .Get(ItemDetailFixture.SimilarPath(ItemDetailPayloads.MovieId), ItemDetailFixture.Ok(ItemDetailPayloads.MovieSimilar));

        var (page, sheet) = await AttachAsync(ItemDetailPayloads.MovieId);

        Assert.False(page.OffersWatchParty);
        Assert.False(sheet.IsOffered);
    }

    [Fact]
    public async Task TheEntry_GoesAway_WhileThisProfileIsInARoom_AndComesBackOnceItLeft()
    {
        _page.Movie();
        var (_, sheet) = await AttachAsync(ItemDetailPayloads.MovieId);
        _page.Silo.On(HttpMethod.Post, "api/v2/watch-together/join", Answered(WatchPartyPayloads.Room(role: "guest")));

        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        Assert.False(sheet.IsOffered);

        await _party.LeaveAsync();
        Assert.True(sheet.IsOffered);
    }

    [Fact]
    public async Task StartingAParty_StagesTheMovie_CopiesTheInvite_AndOpensTheRoom()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        Assert.Equal(("Masterplan", "2026", false), (sheet.Title, sheet.Caption, sheet.HasEpisodeChoice));
        Assert.Equal((true, "This is up next. You start it when everyone is here."), (sheet.IsHostPick, sheet.HostPickCaption));
        Assert.Equal(("Create party & copy invite", false), (sheet.CreateLabel, sheet.GuestsCanPause));
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Contains("\"selection_mode\":\"host_pick\"", Assert.Single(_page.Silo.SentTo(HttpMethod.Post, Rooms)).Body, StringComparison.Ordinal);
        Assert.Equal("""{"content_id":"movie-tmdb-1001"}""", Assert.Single(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection")).Body);
        // The room started host-only, as the profile saved it: nothing to change.
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Patch, Room + "/policy"));
        Assert.Equal(["https://nas.example/silo/rooms/join?token=invite-secret"], _copied);
        Assert.Equal(Invite, Arrival()?.Notice);
        Assert.True(sheet.IsDone);
        Assert.True(_party.IsInRoom);
        Assert.Equal("Masterplan · 2026", await RecentTitleAsync());
    }

    [Fact]
    public async Task InAVote_TheEpisodeGoesInAsTheFirstSuggestion_NotUpNext()
    {
        _page.Episode();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room(mode: "vote")));
        _page.Silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.Created, """{ "suggestion_id": "s1" }"""));
        _page.Silo.On(HttpMethod.Get, Room + "/suggestions", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Suggestions()));
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);

        Assert.Equal("S3 E1 \"Chapter 1\"", sheet.EpisodeText);
        sheet.IsVoteMode = true;
        Assert.Equal("This is the first suggestion. Anyone can add and vote.", sheet.VoteCaption);
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Contains("\"selection_mode\":\"vote\"", Assert.Single(_page.Silo.SentTo(HttpMethod.Post, Rooms)).Body, StringComparison.Ordinal);
        var suggestion = JsonNode.Parse(Assert.Single(_page.Silo.SentTo(HttpMethod.Post, Room + "/suggestions")).Body!)!;
        Assert.Equal(("episode-tmdb-96677-3-1", "episode", "Chapter 1", "Lupin · S3 E1"), ((string?)suggestion["content_id"], (string?)suggestion["content_type"],
            (string?)suggestion["title"], (string?)suggestion["subtitle"]));
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.Equal(Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task AStageThatFails_StillLandsInTheRoom_AndSaysSo()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", ScriptedSilo.Problem(422, "validation_failed"));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Room created, but that item couldn't be queued. Pick it from the room.\n" + Invite, Arrival()?.Notice);
        Assert.True(sheet.IsDone);
        // Nothing is up next, so the recent parties do not name the room after the title.
        Assert.Null(await RecentTitleAsync());
    }

    [Fact]
    public async Task ACreateThatFails_SaysTheServersWords_AndTheSheetStaysOpen()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, ScriptedSilo.Problem(404, "not_found"), ScriptedSilo.NetworkError());
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        await sheet.CreateCommand.ExecuteAsync(null);
        Assert.Equal("Problem detail", sheet.Error);

        await sheet.CreateCommand.ExecuteAsync(null);
        Assert.Equal("Could not create the room", sheet.Error);

        Assert.False(sheet.IsDone);
        Assert.False(_party.IsInRoom);
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.DoesNotContain(_page.Navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
    }

    [Fact]
    public async Task AProfileChangedWhileTheRoomIsMade_EndsItQuietly()
    {
        _page.Movie();
        var answered = new TaskCompletionSource();
        _page.Silo.On(HttpMethod.Post, Rooms, async () =>
        {
            await answered.Task;
            return await Created(WatchPartyPayloads.Room())();
        });
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        var creating = sheet.CreateCommand.ExecuteAsync(null);
        _page.Session.ProfileId = "8";
        _page.Session.RaiseChanged();
        answered.SetResult();
        await creating;

        Assert.Equal(("", true), (sheet.Error, sheet.IsDone));
        Assert.False(_party.IsInRoom);
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.Empty(_copied);
        Assert.DoesNotContain(_page.Navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
    }

    [Fact]
    public async Task AProfileChangedWhileTheSheetIsOpen_MakesNoRoomForTheNextProfile()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        _page.Session.ProfileId = "8";
        _page.Session.RaiseChanged();
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.True(sheet.IsDone);
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Post, Rooms));
    }

    [Fact]
    public async Task ThePageLeft_ClosesTheSheet()
    {
        _page.Movie();
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        sheet.Detach();

        Assert.True(sheet.IsDone);
        Assert.False(sheet.IsOffered);
    }

    [Fact]
    public async Task ASheetCancelledWhileTheRoomIsMade_JoinsIt_ButLeavesTheViewerWhereTheyAre()
    {
        _page.Movie();
        var answered = new TaskCompletionSource();
        _page.Silo.On(HttpMethod.Post, Rooms, async () =>
        {
            await answered.Task;
            return await Created(WatchPartyPayloads.Room())();
        });
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        var creating = sheet.CreateCommand.ExecuteAsync(null);
        sheet.Close();
        // Opening it again waits for the room on its way.
        Assert.False(sheet.Open());
        answered.SetResult();
        await creating;

        Assert.True(_party.IsInRoom);
        Assert.Single(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.Empty(_copied);
        Assert.DoesNotContain(_page.Navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
    }

    [Fact]
    public async Task ASeasonPickedWhileAnotherIsRead_IsTheOneListed()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), ItemDetailFixture.Ok(ItemDetailPayloads.Episode))
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 3), ItemDetailFixture.Ok(ItemDetailPayloads.SeasonEpisodes))
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 2), () => slow.Task)
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 1), ItemDetailFixture.Ok(ItemDetailPayloads.SeasonOneEpisodes));
        var sheet = await OpenAsync(ItemDetailPayloads.SeriesId);
        await sheet.ChooseCommand.ExecuteAsync(null);

        var second = sheet.PickSeasonCommand.ExecuteAsync(sheet.Seasons[1]);
        await sheet.PickSeasonCommand.ExecuteAsync(sheet.Seasons[0]);
        slow.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ItemDetailPayloads.SeasonEpisodes) });
        await second;

        Assert.Equal("Season 1", Assert.Single(sheet.Seasons, s => s.IsSelected).Label);
        Assert.Equal("Pilot", Assert.Single(sheet.Episodes).Title);
    }

    [Fact]
    public async Task TheSwitch_FollowsTheSavedChoice_WhichTheNewRoomAlreadyHas()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Get, Effective, ScriptedSilo.Json(HttpStatusCode.OK, Saved("guest_play_pause")));
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room(policy: "guest_play_pause")));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(policy: "guest_play_pause")));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        Assert.True(sheet.GuestsCanPause);
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Empty(_page.Silo.SentTo(HttpMethod.Patch, Room + "/policy"));
        Assert.Equal(Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task AFlippedSwitch_IsAppliedToTheRoom()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Get, Effective, ScriptedSilo.Json(HttpStatusCode.OK, Saved("guest_play_pause")));
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room(policy: "guest_play_pause")));
        _page.Silo.On(HttpMethod.Patch, Room + "/policy", Answered(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room()));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        sheet.GuestsCanPause = false;
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("""{"guest_control_policy":"host_only"}""", Assert.Single(_page.Silo.SentTo(HttpMethod.Patch, Room + "/policy")).Body);
        Assert.Equal(Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task ASwitchTheRoomRefused_IsSaid()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Patch, Room + "/policy", ScriptedSilo.Problem(409, "capability_disabled"));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room()));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        sheet.GuestsCanPause = true;
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Room created, but guests can't pause yet. Change it from the room.\n" + Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task AnUnreadSavedChoice_LeavesTheRoomsOwn_AndSaysWhatItIs()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Get, Effective, ScriptedSilo.Problem(422, "validation_failed"));
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room(policy: "guest_play_pause")));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(policy: "guest_play_pause")));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        Assert.False(sheet.GuestsCanPause);
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Empty(_page.Silo.SentTo(HttpMethod.Patch, Room + "/policy"));
        Assert.Equal("Guests can pause in this party. Change it from the room.\n" + Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task ASeries_PutsUpItsNextEpisode()
    {
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), ItemDetailFixture.Ok(ItemDetailPayloads.Episode));

        var sheet = await OpenAsync(ItemDetailPayloads.SeriesId);

        Assert.Equal(("Lupin", "Series · 3 seasons", true), (sheet.Title, sheet.Caption, sheet.HasEpisodeChoice));
        Assert.Equal(new StartPartyTarget(ItemDetailPayloads.EpisodeId, "episode", "Chapter 1", "S3 E1", "Lupin"), sheet.Target);
        Assert.Equal(("S3 E1 \"Chapter 1\"", "Change"), (sheet.EpisodeText, sheet.ChangeLabel));
        Assert.True(sheet.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASeriesWhoseNextEpisodeCannotBeRead_AsksForOne_AndCanStillOpenAnEmptyRoom()
    {
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), ItemDetailFixture.Busy());
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));

        var sheet = await OpenAsync(ItemDetailPayloads.SeriesId);

        Assert.Null(sheet.Target);
        Assert.Equal(("Choose an episode", "Choose", "Nothing is up next. You start it when everyone is here."),
            (sheet.EpisodeText, sheet.ChangeLabel, sheet.HostPickCaption));
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Empty(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.Equal(Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task ASeason_PutsUpItsFirstUnwatchedEpisode()
    {
        _page.Season();

        var sheet = await OpenAsync(ItemDetailPayloads.Season3Id);

        Assert.Equal(("Lupin", "Staffel 3"), (sheet.Title, sheet.Caption));
        Assert.Equal(new StartPartyTarget(ItemDetailPayloads.Episode2Id, "episode", "Chapter 2", "S3 E2", "Lupin"), sheet.Target);
    }

    [Fact]
    public async Task ChoosingAnotherEpisode_ListsThoseWithAFile_AndPutsUpThePick()
    {
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), ItemDetailFixture.Ok(ItemDetailPayloads.Episode))
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 3), ItemDetailFixture.Ok(ItemDetailPayloads.SeasonEpisodes))
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 1), ItemDetailFixture.Ok(ItemDetailPayloads.SeasonOneEpisodes));
        var sheet = await OpenAsync(ItemDetailPayloads.SeriesId);

        await sheet.ChooseCommand.ExecuteAsync(null);

        Assert.True(sheet.IsChoosing);
        Assert.False(sheet.CreateCommand.CanExecute(null));
        Assert.Equal(["Season 1", "Season 2", "Season 3", "Specials"], sheet.Seasons.Select(s => s.Label));
        Assert.Equal("Season 3", Assert.Single(sheet.Seasons, s => s.IsSelected).Label);
        Assert.Equal(["Episode 1, Chapter 1, watched", "Episode 2, Chapter 2"], sheet.Episodes.Select(e => e.AccessibleName));

        await sheet.PickSeasonCommand.ExecuteAsync(sheet.Seasons[0]);
        // Season 1's second episode has no file to play.
        var pilot = Assert.Single(sheet.Episodes);
        Assert.Equal(("E1", "Pilot"), (pilot.Number, pilot.Title));
        sheet.PickEpisodeCommand.Execute(pilot);

        Assert.False(sheet.IsChoosing);
        Assert.Equal(new StartPartyTarget("episode-tmdb-96677-1-1", "episode", "Pilot", "S1 E1", "Lupin"), sheet.Target);
        Assert.True(sheet.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task SeasonsThatCannotBeRead_LeaveThePickAsItWas()
    {
        _page.Episode();
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);
        _page.Silo.On(HttpMethod.Get, ItemDetailFixture.SeasonsPath(ItemDetailPayloads.SeriesId), ItemDetailFixture.Busy());

        await sheet.ChooseCommand.ExecuteAsync(null);

        Assert.False(sheet.IsChoosing);
        Assert.True(sheet.ShowsPick);
        Assert.Equal(ItemDetailPayloads.EpisodeId, sheet.Target?.ContentId);
    }

    [Fact]
    public async Task KeepingTheCurrentPick_LeavesItUp()
    {
        _page.Episode().Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 1), ItemDetailFixture.Ok(ItemDetailPayloads.NoEpisodes));
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);
        var before = sheet.Target;

        await sheet.ChooseCommand.ExecuteAsync(null);
        await sheet.PickSeasonCommand.ExecuteAsync(sheet.Seasons[0]);
        Assert.True(sheet.HasNoEpisodes);
        sheet.KeepCurrentCommand.Execute(null);

        Assert.False(sheet.IsChoosing);
        Assert.Equal(before, sheet.Target);
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
