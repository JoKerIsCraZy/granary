using System.Net;
using System.Text.Json.Nodes;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.ViewModels.WatchParty;
using Granary.Tests.Browse;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

// What keeps the start sheet safe: a failure of any kind is said rather than thrown, every step is for the server and
// profile the sheet was opened for, a late answer of an earlier opening is dropped, and a sheet put away mid-create
// leaves the viewer where they are.
public sealed partial class StartPartyTests
{
    private static Func<Task<HttpResponseMessage>> Throws() =>
        () => Task.FromException<HttpResponseMessage>(new NotSupportedException("Not something a request fails with."));

    private static (Func<Task<HttpResponseMessage>> Responder, TaskCompletionSource<HttpResponseMessage> Gate) Held() => ItemDetailFixture.Held();

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task ACreateFailingInAnUnexpectedWay_IsSaid_NotThrown()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Throws());
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal(("Could not create the room", false, false), (sheet.Error, sheet.IsDone, sheet.IsBusy));
    }

    [Fact]
    public async Task ASheetCancelledOnce_CopiesAndOpensTheRoom_WhenOpenedAgainAndCreated()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);
        sheet.Close();

        Assert.True(sheet.Open());
        await sheet.Opening;
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Single(_copied);
        Assert.Equal(Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task ARoomPageThatCannotOpen_StillPutsTheSheetAway()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId, Sheet(new FailingNavigation()));

        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal(("", true), (sheet.Error, sheet.IsDone));
        Assert.True(_party.IsInRoom);
    }

    [Fact]
    public async Task SeasonsFailingInAnUnexpectedWay_LeaveThePickAsItWas()
    {
        _page.Episode();
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);
        _page.Silo.On(HttpMethod.Get, ItemDetailFixture.SeasonsPath(ItemDetailPayloads.SeriesId), Throws());

        await sheet.ChooseCommand.ExecuteAsync(null);

        Assert.False(sheet.IsChoosing);
        Assert.Equal(ItemDetailPayloads.EpisodeId, sheet.Target?.ContentId);
    }

    [Fact]
    public async Task ASeasonsEpisodesFailingInAnUnexpectedWay_EndTheLoading()
    {
        _page.Episode().Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 1), Throws());
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);
        await sheet.ChooseCommand.ExecuteAsync(null);

        await sheet.PickSeasonCommand.ExecuteAsync(sheet.Seasons[0]);

        Assert.False(sheet.IsLoadingEpisodes);
        Assert.Empty(sheet.Episodes);
    }

    [Fact]
    public async Task TheServersRooms_ReadInAnUnexpectedWay_AreQuiet()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Get, Capabilities, Throws());
        var page = await _page.LoadAsync(ItemDetailPayloads.MovieId);
        var sheet = Sheet();

        sheet.Attach(page);
        await sheet.Refreshing;

        Assert.False(sheet.IsOffered);
    }

    [Fact]
    public async Task TheServersRooms_ReadForTheProfileBefore_AreNotTakenForTheNextOne()
    {
        var (responder, gate) = Held();
        _page.Silo.On(HttpMethod.Get, Capabilities, responder);

        var reading = _feature.RefreshAsync(TestContext.Current.CancellationToken);
        _page.Session.ProfileId = "8";
        gate.SetResult(Ok(Hosting));
        await reading;
        Assert.Null(_feature.Capabilities);

        // The profile before, picked again, reads them again.
        _page.Session.ProfileId = "7";
        _page.Silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK, Hosting));
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(_feature.IsAvailable);
    }

    [Fact]
    public async Task CreateTwice_MakesOneRoom()
    {
        _page.Movie();
        var (responder, gate) = Held();
        _page.Silo.On(HttpMethod.Post, Rooms, responder);
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        var first = sheet.CreateCommand.ExecuteAsync(null);
        var second = sheet.CreateCommand.ExecuteAsync(null);
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(WatchPartyPayloads.Answer(WatchPartyPayloads.Room())) });
        await Task.WhenAll(first, second);

        Assert.Single(_page.Silo.SentTo(HttpMethod.Post, Rooms));
        Assert.Single(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
    }

    [Fact]
    public async Task AServerChangedBetweenTheSteps_StopsThere()
    {
        _page.Movie();
        var (responder, gate) = Held();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Patch, Room + "/policy", responder);
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);
        sheet.GuestsCanPause = true;

        var creating = sheet.CreateCommand.ExecuteAsync(null);
        await ServerPlaybackFixture.Until(() => _page.Silo.SentTo(HttpMethod.Patch, Room + "/policy").Count == 1, "the pause choice");
        _page.Session.ActiveServer = ScriptedSilo.Entry("srv2", ScriptedSilo.Base);
        _page.Session.RaiseChanged();
        gate.SetResult(Ok(WatchPartyPayloads.Answer(WatchPartyPayloads.Room(policy: "guest_play_pause"))));
        await creating;

        Assert.True(sheet.IsDone);
        Assert.False(_party.IsInRoom);
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Put, Room + "/staged-selection"));
        Assert.Empty(_copied);
        Assert.DoesNotContain(_page.Navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
    }

    [Fact]
    public async Task APauseChoiceTheRoomDidNotTake_IsSaid()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        // The room answers, still host-only.
        _page.Silo.On(HttpMethod.Patch, Room + "/policy", Answered(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room()));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId);

        sheet.GuestsCanPause = true;
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Room created, but guests can't pause yet. Change it from the room.\n" + Invite, Arrival()?.Notice);
    }

    [Fact]
    public async Task AFirstSuggestionTheRoomRefused_IsSaid_AndNamesNoRoom()
    {
        _page.Episode();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room(mode: "vote")));
        _page.Silo.On(HttpMethod.Post, Room + "/suggestions", ScriptedSilo.Problem(409, "conflict"));
        var sheet = await OpenAsync(ItemDetailPayloads.EpisodeId);

        sheet.IsVoteMode = true;
        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Problem detail\n" + Invite, Arrival()?.Notice);
        Assert.Null(await RecentTitleAsync());
    }

    [Fact]
    public async Task AnInviteTheClipboardRefused_IsSaid()
    {
        _page.Movie();
        _page.Silo.On(HttpMethod.Post, Rooms, Created(WatchPartyPayloads.Room()));
        _page.Silo.On(HttpMethod.Put, Room + "/staged-selection", Answered(WatchPartyPayloads.Room(selected: ItemDetailPayloads.MovieId)));
        var sheet = await OpenAsync(ItemDetailPayloads.MovieId, Sheet(copies: false));

        await sheet.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Failed to copy invite link", Arrival()?.Notice);
    }

    [Fact]
    public async Task TheNextEpisodeNamedLate_ForAClosedOrEarlierOpening_IsDropped()
    {
        var (responder, gate) = Held();
        var older = JsonNode.Parse(ItemDetailPayloads.Episode)!;
        older["title"] = "An older answer";
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), responder, ItemDetailFixture.Ok(ItemDetailPayloads.Episode));
        var (_, sheet) = await AttachAsync(ItemDetailPayloads.SeriesId);

        Assert.True(sheet.Open());
        var first = sheet.Opening;
        sheet.Close();
        Assert.False(sheet.IsNamingTarget);
        Assert.True(sheet.Open());
        await sheet.Opening;
        gate.SetResult(Ok(older.ToJsonString()));
        await first;

        Assert.Equal("Chapter 1", sheet.Target?.Title);
    }

    [Fact]
    public async Task ASheetOpenedWithoutAReadySession_DoesNotWaitForTheNextEpisode()
    {
        _page.Series();
        var (_, sheet) = await AttachAsync(ItemDetailPayloads.SeriesId);
        _page.Session.State = SessionState.ProfileRequired;

        Assert.True(sheet.Open());
        await sheet.Opening;

        Assert.False(sheet.IsNamingTarget);
        Assert.Equal("Choose an episode", sheet.EpisodeText);
        Assert.Empty(_page.Silo.SentTo(HttpMethod.Get, ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId)));
    }

    [Fact]
    public async Task AnEpisodePickedWhileTheNextOneIsNamed_StaysPicked()
    {
        var (responder, gate) = Held();
        _page.Series().Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.EpisodeId), responder)
            .Get(ItemDetailFixture.SeriesEpisodesPath(ItemDetailPayloads.SeriesId, 3), ItemDetailFixture.Ok(ItemDetailPayloads.SeasonEpisodes));
        var (_, sheet) = await AttachAsync(ItemDetailPayloads.SeriesId);
        Assert.True(sheet.Open());
        Assert.True(sheet.IsNamingTarget);

        await sheet.ChooseCommand.ExecuteAsync(null);
        sheet.PickEpisodeCommand.Execute(sheet.Episodes[1]);
        Assert.False(sheet.CreateCommand.CanExecute(null));
        gate.SetResult(Ok(ItemDetailPayloads.Episode));
        await sheet.Opening;

        Assert.Equal(ItemDetailPayloads.Episode2Id, sheet.Target?.ContentId);
        Assert.True(sheet.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEntry_FollowsThePageItIsAttachedTo_Only()
    {
        const string BareId = "movie-tmdb-2002";
        var bare = JsonNode.Parse(ItemDetailPayloads.Movie)!;
        bare["content_id"] = BareId;
        bare["versions"] = new JsonArray();
        _page.Movie().Get(ItemDetailFixture.ItemPath(BareId), ItemDetailFixture.Ok(bare.ToJsonString()))
            .Get(ItemDetailFixture.SimilarPath(BareId), ItemDetailFixture.Ok(ItemDetailPayloads.MovieSimilar));
        var withFile = await _page.LoadAsync(ItemDetailPayloads.MovieId);
        var withoutFile = await _page.LoadAsync(BareId);
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        var sheet = Sheet();

        sheet.Attach(withFile);
        sheet.Attach(withFile);
        Assert.True(sheet.IsOffered);
        sheet.Detach();
        Assert.False(sheet.IsOffered);
        sheet.Attach(withoutFile);
        Assert.False(sheet.IsOffered);
        sheet.Attach(withFile);
        Assert.True(sheet.IsOffered);
    }

    [Fact]
    public async Task ASeason_OffersAPartyOnceItsEpisodesAreListed()
    {
        var (responder, gate) = Held();
        _page.Get(ItemDetailFixture.ItemPath(ItemDetailPayloads.Season3Id), ItemDetailFixture.Ok(ItemDetailPayloads.Season))
            .Get(ItemDetailFixture.SeasonItemEpisodesPath(ItemDetailPayloads.Season3Id), responder);
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        var page = _page.Create();
        var sheet = Sheet();
        sheet.Attach(page);

        var loading = page.LoadCommand.ExecuteAsync(ItemDetailPayloads.Season3Id);
        await ServerPlaybackFixture.Until(() => page.IsReady, "the season");
        Assert.False(sheet.IsOffered);
        gate.SetResult(Ok(ItemDetailPayloads.SeasonEpisodes));
        await loading;

        Assert.True(sheet.IsOffered);
        Assert.True(sheet.Open());
        Assert.Equal(ItemDetailPayloads.Episode2Id, sheet.Target?.ContentId);
    }

    /// <summary>A frame that cannot show the room page.</summary>
    private sealed class FailingNavigation : INavigationService
    {
        public AppRoute? Current => null;

        public bool CanGoBack => false;

        public void NavigateTo(AppRoute route, object? parameter = null) => throw new InvalidOperationException("The frame is gone.");

        public void ResetTo(AppRoute route, object? parameter = null)
        {
        }

        public void GoBack()
        {
        }

        public void ShowSection(AppRoute route, object? parameter = null)
        {
        }

        public void LeavePlayer()
        {
        }
    }
}
