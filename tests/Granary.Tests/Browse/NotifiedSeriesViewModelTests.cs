using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Notifications;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The notifications page's Series view: which series notify the profile, which won't and why, and Mute.</summary>
public sealed class NotifiedSeriesViewModelTests
{
    private const string SeriesPath = "api/v2/notifications/series";
    private const string PreferencesPath = "api/v2/notifications/preferences";
    private const string CapabilitiesPath = "api/v2/notifications/capabilities";
    private const string ListPath = "api/v2/notifications";
    private const string MutePath = "api/v2/notifications/series/series%3Adark/mute";

    /// <summary>Dark notifies as a favourite; Severance only on Next Up, after S2E9.</summary>
    private const string Notifying = """
        {
          "items": [
            { "item": { "content_id": "series:dark", "type": "series", "title": "Dark", "poster_url": "/api/v2/artwork/d.webp?sig=d" },
              "reasons": { "favorite": true, "watchlist": true, "continue_watching": false, "next_up": false },
              "active_reasons": { "favorite": true, "watchlist": false, "continue_watching": false, "next_up": false },
              "muted": false, "notifies": true },
            { "item": { "content_id": "series:severance", "type": "series", "title": "Severance" },
              "reasons": { "favorite": false, "watchlist": false, "continue_watching": false, "next_up": true },
              "active_reasons": { "favorite": false, "watchlist": false, "continue_watching": false, "next_up": true },
              "muted": false, "notifies": true, "next_up_after": { "season_number": 2, "episode_number": 9 } }
          ],
          "page": { "has_more": false }
        }
        """;

    /// <summary>Lost is muted; Fringe's only reason, the watchlist, is switched off.</summary>
    private const string Silenced = """
        {
          "items": [
            { "item": { "content_id": "series:lost", "type": "series", "title": "Lost" },
              "reasons": { "favorite": true, "watchlist": false, "continue_watching": false, "next_up": false },
              "active_reasons": { "favorite": true, "watchlist": false, "continue_watching": false, "next_up": false },
              "muted": true, "notifies": false },
            { "item": { "content_id": "series:fringe", "type": "series", "title": "Fringe" },
              "reasons": { "favorite": false, "watchlist": true, "continue_watching": false, "next_up": false },
              "active_reasons": { "favorite": false, "watchlist": false, "continue_watching": false, "next_up": false },
              "muted": false, "notifies": false }
          ],
          "page": { "has_more": false }
        }
        """;

    private const string None = """{ "items": [], "page": { "has_more": false } }""";

    private const string Preferences = """
        { "enabled": true, "notify_favorites": true, "notify_watchlist": false, "notify_continue_watching": true, "notify_next_up": true }
        """;

    private readonly LibraryHarness _harness = new();
    private readonly Dictionary<string, string> _pages = new(StringComparer.Ordinal);

    public NotifiedSeriesViewModelTests()
    {
        _pages["active"] = Notifying;
        _pages["inactive"] = Silenced;
        // The two lists share a path: each answer is the page its status (and cursor) asks for.
        _harness.Answer(SeriesPath, ByQuery);
        _harness.Answer(PreferencesPath, Preferences);
        _harness.Answer(CapabilitiesPath, """{ "in_app": { "enabled": true }, "series": { "available": true, "mute": true } }""");
        _harness.Answer(ListPath, """{ "items": [], "page": { "has_more": false }, "read_cutoff": "cut-0" }""");
        _harness.Answer("api/v2/notifications/unread-count", """{ "count": 0 }""");
        _harness.Silo.On(HttpMethod.Put, MutePath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, MutePath, ScriptedSilo.NoContent());
    }

    // The next this many series reads wait for the gate.
    private int _held;
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NotificationBadge? _badge;

    // The page a series read asks for: "active", "active|favorite" when such a page is set, "inactive:i2" for a cursor.
    private async Task<HttpResponseMessage> ByQuery()
    {
        var sent = _harness.Silo.Sent.Last(r => r.Uri.AbsolutePath.EndsWith(SeriesPath, StringComparison.Ordinal));
        var query = System.Web.HttpUtility.ParseQueryString(sent.Uri.Query);
        string status = query["status"]!;
        string key = query["reason"] is { } reason && _pages.ContainsKey($"{status}|{reason}") ? $"{status}|{reason}" : status;
        if (query["cursor"] is { } cursor)
        {
            key += ":" + cursor;
        }
        string body = _pages[key];
        if (_held > 0)
        {
            _held--;
            await _gate.Task;
        }
        return await ScriptedSilo.Json(HttpStatusCode.OK, body)();
    }

    private NotificationsViewModel CreatePage()
    {
        var api = new NotificationsApi(_harness.Silo.Http);
        _badge = new NotificationBadge(_harness.Session, api, _harness.Clock, _harness.Logs.For<NotificationBadge>());
        return new(_harness.Session, api, _badge, _harness.Navigation, _harness.Clock, _harness.Logs.For<NotificationsViewModel>());
    }

    private async Task<NotificationsViewModel> SeriesViewAsync()
    {
        var page = CreatePage();
        await page.ActivateAsync(isReturn: false);
        page.ShowSeriesCommand.Execute(null);
        await WaitUntilAsync(() => page.Series.State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Error);
        return page;
    }

    private List<string> SeriesQueries() => _harness.Queries(SeriesPath);

    [Fact]
    public async Task TheSeries_ShowAsTheWebWordsThem()
    {
        var series = (await SeriesViewAsync()).Series;

        Assert.Equal(["Dark", "Severance"], series.Notifying.Select(r => r.Title));
        Assert.Equal(["Any new episode", "Any episode after S2E9"], series.Notifying.Select(r => r.Line));
        Assert.Equal([new ReasonPill("Favorite", false), new ReasonPill("Watchlist", true)], series.Notifying[0].Pills);
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/d.webp?sig=d"), series.Notifying[0].PosterUrl);
        Assert.True(series.ShowsSilenced);
        Assert.Equal(["Lost", "Fringe"], series.Silenced.Select(r => r.Title));
        Assert.Equal(["Muted", "Watchlist notifications are off"], series.Silenced.Select(r => r.Line));
        Assert.Equal([new ReasonPill("Favorite", true)], series.Silenced[0].Pills);
        Assert.Equal(["Mute", "Mute", "Unmute", "Mute"], series.Notifying.Concat(series.Silenced).Select(r => r.MuteLabel));
        Assert.True(series.CanMute);
        Assert.Equal(", or mute one series.", series.IntroEnd);
        Assert.Contains("status=active&limit=50&image_size=small&sort=title", SeriesQueries());
        Assert.Contains("status=inactive&limit=50&image_size=small&sort=title", SeriesQueries());
    }

    [Fact]
    public void Silenced_NamesTheSwitchesThatSilenceIt()
    {
        var all = new NotifiedSeries
        {
            Reasons = new NotificationSeriesReasons(Favorite: true, Watchlist: true, ContinueWatching: false, NextUp: true),
        };
        var off = new NotificationPreferences { Enabled = true, NotifyContinueWatching = true };

        Assert.Equal("Favorites, Watchlist and Next Up notifications are off", NotifiedSeriesText.Silenced(all, off));
        Assert.Equal("Notifications are off", NotifiedSeriesText.Silenced(all, off with { Enabled = false }));
        Assert.Equal("Won’t notify right now", NotifiedSeriesText.Silenced(all, preferences: null));
        Assert.Equal("Won’t notify right now",
            NotifiedSeriesText.Silenced(all, off with { NotifyFavorites = true, NotifyWatchlist = true, NotifyNextUp = true }));
    }

    [Fact]
    public async Task TheNotifyingSeries_LoadFirst_ThenTheOthers()
    {
        _pages["active"] = Notifying.Replace("\"has_more\": false }", "\"has_more\": true, \"next_cursor\": \"a2\" }", StringComparison.Ordinal);
        _pages["inactive"] = Silenced.Replace("\"has_more\": false }", "\"has_more\": true, \"next_cursor\": \"i2\" }", StringComparison.Ordinal);
        _pages["active:a2"] = None.Replace("[]", """[ { "item": { "content_id": "series:ozark", "title": "Ozark" }, "notifies": true } ]""", StringComparison.Ordinal);
        _pages["inactive:i2"] = None.Replace("[]", """[ { "item": { "content_id": "series:alf", "title": "ALF" }, "notifies": false } ]""", StringComparison.Ordinal);
        var series = (await SeriesViewAsync()).Series;
        Assert.False(series.ShowsSilenced);

        await series.LoadMoreOnScrollCommand.ExecuteAsync(null);
        Assert.Equal("Ozark", series.Notifying[^1].Title);
        Assert.True(series.ShowsSilenced);
        Assert.True(series.CanLoadMore);

        await series.LoadMoreOnScrollCommand.ExecuteAsync(null);
        Assert.Equal("ALF", series.Silenced[^1].Title);
        Assert.False(series.CanLoadMore);
        Assert.Equal("status=inactive&limit=50&cursor=i2&image_size=small&sort=title", SeriesQueries()[^1]);
    }

    [Fact]
    public async Task AFailedPage_WaitsForTryAgain()
    {
        _pages["active"] = Notifying.Replace("\"has_more\": false }", "\"has_more\": true, \"next_cursor\": \"a2\" }", StringComparison.Ordinal);
        var series = (await SeriesViewAsync()).Series;
        _harness.Answer(SeriesPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));

        await series.LoadMoreOnScrollCommand.ExecuteAsync(null);
        Assert.Equal("Couldn’t load more series.", series.LoadMoreError);
        int asked = SeriesQueries().Count;

        await series.LoadMoreOnScrollCommand.ExecuteAsync(null);
        Assert.Equal(asked, SeriesQueries().Count);

        _pages["active:a2"] = None;
        _harness.Answer(SeriesPath, ByQuery);
        await series.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(series.HasLoadMoreError);
        Assert.Equal(asked + 1, SeriesQueries().Count);
    }

    [Fact]
    public async Task TheSearch_WaitsForAPause_AndSaysWhenNothingMatches()
    {
        var series = (await SeriesViewAsync()).Series;
        int asked = SeriesQueries().Count;
        _pages["active"] = None;
        _pages["inactive"] = None;

        series.Search = "da";
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        series.Search = "dar ";
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(asked, SeriesQueries().Count);

        _harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await series.Searching;

        Assert.Contains("status=active&limit=50&image_size=small&q=dar&sort=title", SeriesQueries());
        Assert.True(series.IsFilteredEmpty);
        Assert.False(series.IsBareEmpty);
    }

    [Fact]
    public async Task AReasonOrSort_ReadsBothListsAgain()
    {
        var series = (await SeriesViewAsync()).Series;

        series.Reason = NotifiedSeriesViewModel.ReasonChoices[1];
        await WaitUntilAsync(() => SeriesQueries().Count(q => q.Contains("reason=favorite", StringComparison.Ordinal)) == 2);
        series.Sort = NotifiedSeriesViewModel.SortChoices[4];

        await WaitUntilAsync(() => SeriesQueries().Contains("status=inactive&limit=50&image_size=small&reason=favorite&sort=-year"));
        Assert.Equal("Title (A–Z)", NotifiedSeriesViewModel.SortChoices[0].Label);
    }

    [Fact]
    public async Task Mute_SaysSo_CanBeUndone_AndReadsTheListsAgain()
    {
        var series = (await SeriesViewAsync()).Series;
        int asked = SeriesQueries().Count;

        await series.ToggleMuteCommand.ExecuteAsync(series.Notifying[0]);

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, MutePath));
        Assert.Equal("Muted Dark", series.Notice);
        Assert.False(series.NoticeFailed);
        Assert.True(series.CanUndo);
        Assert.Equal(asked + 2, SeriesQueries().Count);

        await series.UndoCommand.ExecuteAsync(null);

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, MutePath));
        Assert.False(series.HasNotice);
        Assert.False(series.CanUndo);
    }

    [Fact]
    public async Task AFailedMute_SaysSo()
    {
        _harness.Silo.On(HttpMethod.Put, MutePath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var series = (await SeriesViewAsync()).Series;

        await series.ToggleMuteCommand.ExecuteAsync(series.Notifying[0]);

        Assert.Equal("Failed to mute the series", series.Notice);
        Assert.True(series.NoticeFailed);
        Assert.False(series.CanUndo);
        Assert.False(series.Notifying[0].IsPending);
    }

    [Fact]
    public async Task ARow_OpensItsSeries()
    {
        var series = (await SeriesViewAsync()).Series;

        series.OpenCommand.Execute(series.Silenced[1]);

        Assert.Equal((AppRoute.Item, (object?)"series:fringe"), Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task ListsThatFail_OfferRetry()
    {
        _harness.Answer(SeriesPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var series = (await SeriesViewAsync()).Series;
        Assert.True(series.HasError);

        _harness.Answer(SeriesPath, ByQuery);
        await series.LoadCommand.ExecuteAsync(null);

        Assert.True(series.IsReady);
        Assert.Equal(2, series.Silenced.Count);
    }

    [Fact]
    public async Task NoSeries_SaysWhatShowsUpHere()
    {
        _pages["active"] = None;
        _pages["inactive"] = None;

        var series = (await SeriesViewAsync()).Series;

        Assert.True(series.IsBareEmpty);
    }

    [Fact]
    public async Task ASavedPreference_ReadsTheSeriesAgain()
    {
        var page = await SeriesViewAsync();
        int asked = SeriesQueries().Count;
        _harness.Silo.On(HttpMethod.Put, PreferencesPath, ScriptedSilo.Json(HttpStatusCode.OK, Preferences.Replace("\"notify_watchlist\": false", "\"notify_watchlist\": true", StringComparison.Ordinal)));
        await page.Preferences.LoadAsync();

        page.Preferences.NotifyWatchlist = true;

        await WaitUntilAsync(() => SeriesQueries().Count == asked + 2);
    }

    [Fact]
    public async Task Series_IsAFilter_OnlyWhereTheServerListsThem()
    {
        var page = await SeriesViewAsync();
        Assert.Equal(["All", "Unread", "Series"], page.Filters.Select(f => f.Label));
        Assert.True(page.IsSeriesView);
        Assert.False(page.ShowsMarkAllRead);

        _harness.Answer(CapabilitiesPath, """{ "in_app": { "enabled": true }, "series": { "available": false, "mute": false } }""");
        var plain = CreatePage();

        await plain.ActivateAsync(isReturn: false);
        plain.ShowSeriesCommand.Execute(null);

        Assert.Equal(["All", "Unread"], plain.Filters.Select(f => f.Label));
        Assert.False(plain.IsSeriesView);
    }

    [Fact]
    public async Task TheInbox_StaysAsItWas_AcrossTheSeriesView_AndAFreshVisitOpensIt()
    {
        var page = await SeriesViewAsync();
        int inboxReads = _harness.Count(ListPath);

        page.SelectedFilter = page.Filters[0];
        Assert.True(page.IsInboxView);
        Assert.Equal(inboxReads, _harness.Count(ListPath));

        page.ShowSeriesCommand.Execute(null);
        await page.ActivateAsync(isReturn: true);
        Assert.True(page.IsSeriesView);

        await page.ActivateAsync(isReturn: false);
        Assert.True(page.IsInboxView);
        Assert.Equal(inboxReads + 1, _harness.Count(ListPath));
    }

    [Fact]
    public async Task AnOlderRead_ArrivingLate_IsDropped()
    {
        _pages["active|favorite"] = None.Replace("[]", """[ { "item": { "content_id": "series:ozark", "title": "Ozark" }, "notifies": true } ]""", StringComparison.Ordinal);
        _pages["inactive|favorite"] = None;
        var page = await SeriesViewAsync();
        var series = page.Series;
        _held = 2;
        series.Sort = NotifiedSeriesViewModel.SortChoices[1];
        await WaitUntilAsync(() => series.IsRefreshing && _held == 0);

        series.Reason = NotifiedSeriesViewModel.ReasonChoices[1];
        await WaitUntilAsync(() => !series.IsRefreshing);
        _gate.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["Ozark"], series.Notifying.Select(r => r.Title));
        Assert.Empty(series.Silenced);
    }

    [Fact]
    public async Task NoPageIsAskedFor_WhileTheListsAreReadAgain()
    {
        _pages["active"] = Notifying.Replace("\"has_more\": false }", "\"has_more\": true, \"next_cursor\": \"a2\" }", StringComparison.Ordinal);
        var series = (await SeriesViewAsync()).Series;
        Assert.True(series.LoadMoreCommand.CanExecute(null));
        _held = 2;

        series.Sort = NotifiedSeriesViewModel.SortChoices[2];
        await WaitUntilAsync(() => series.IsRefreshing);

        Assert.False(series.LoadMoreCommand.CanExecute(null));
        Assert.False(series.LoadMoreOnScrollCommand.CanExecute(null));
        _gate.SetResult();
        await WaitUntilAsync(() => !series.IsRefreshing);
        Assert.True(series.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASessionThatEnds_LeavesNothingRefreshing()
    {
        var series = (await SeriesViewAsync()).Series;
        _held = 2;
        series.Sort = NotifiedSeriesViewModel.SortChoices[2];
        await WaitUntilAsync(() => series.IsRefreshing);

        _harness.Session.State = SessionState.NoServer;
        await series.LoadCommand.ExecuteAsync(null);
        _gate.SetResult();

        Assert.False(series.IsRefreshing);
        Assert.Equal(LibraryLoadState.Idle, series.State);
        Assert.Empty(series.Notifying);
    }

    [Fact]
    public async Task AFailedUndo_CanBeTriedAgain()
    {
        _harness.Silo.On(HttpMethod.Delete, MutePath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var series = (await SeriesViewAsync()).Series;
        await series.ToggleMuteCommand.ExecuteAsync(series.Notifying[0]);

        await series.UndoCommand.ExecuteAsync(null);

        Assert.Equal("Failed to unmute the series", series.Notice);
        Assert.True(series.CanUndo);
        _harness.Silo.On(HttpMethod.Delete, MutePath, ScriptedSilo.NoContent());
        await series.UndoCommand.ExecuteAsync(null);
        Assert.False(series.CanUndo);
        Assert.Equal(2, _harness.Silo.SentTo(HttpMethod.Delete, MutePath).Count);
    }

    [Fact]
    public async Task AnotherProfile_LeavesTheLastOnesSeriesBehind()
    {
        var page = await SeriesViewAsync();

        _harness.Session.ProfileId = "8";
        await page.ActivateAsync(isReturn: false);

        Assert.True(page.IsInboxView);
        Assert.Equal(LibraryLoadState.Idle, page.Series.State);
        Assert.Empty(page.Series.Notifying);
    }

    [Fact]
    public async Task CapabilitiesThatFail_OfferNoSeries()
    {
        _harness.Answer(CapabilitiesPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var page = CreatePage();

        await page.ActivateAsync(isReturn: false);
        page.ShowSeriesCommand.Execute(null);

        Assert.Equal(["All", "Unread"], page.Filters.Select(f => f.Label));
        Assert.True(page.IsInboxView);
    }

    [Fact]
    public async Task TheUnreadCount_ChangingInTheSeriesView_KeepsIt()
    {
        var page = await SeriesViewAsync();
        int inboxReads = _harness.Count(ListPath);
        int seriesReads = SeriesQueries().Count;

        _badge!.Show(3);

        Assert.Equal(["All", "Unread (3)", "Series"], page.Filters.Select(f => f.Label));
        Assert.True(page.IsSeriesView);
        Assert.Equal(inboxReads, _harness.Count(ListPath));
        Assert.Equal(seriesReads, SeriesQueries().Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
