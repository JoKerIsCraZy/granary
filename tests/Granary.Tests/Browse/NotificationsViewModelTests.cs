using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Notifications;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The Notifications inbox and the sidebar's unread count; the clock stands on 1 October 2026, 12:00 UTC.</summary>
public sealed class NotificationsViewModelTests
{
    private const string ListPath = "api/v2/notifications";
    private const string CountPath = "api/v2/notifications/unread-count";
    private const string CapabilitiesPath = "api/v2/notifications/capabilities";

    /// <summary>A new episode (unread, five minutes ago), a fulfilled request (read, three days ago), and a declined request.</summary>
    private const string FirstPage = """
        {
          "items": [
            {
              "id": "n1", "type": "episode.available", "profile_id": "7", "created_at": "2026-10-01T11:55:00Z", "read_at": null,
              "series_id": "series:severance", "series_title": "Severance", "episode_id": "episode:severance-s02e01",
              "season_number": 2, "episode_number": 1, "episode_title": "Hello, Ms. Cobel",
              "poster_url": "/api/v2/artwork/s.webp?sig=s", "reason_flags": { "favorite": true, "next_up": true, "unknown": 3 }
            },
            {
              "id": "n2", "type": "request.fulfilled", "profile_id": "7", "created_at": "2026-09-28T12:00:00Z", "read_at": "2026-09-29T08:00:00Z",
              "series_id": "movie:dune-3", "series_title": "Dune: Part Three", "reason_flags": { "media_type": "movie", "follower": true }
            },
            {
              "id": "n3", "type": "request.declined", "profile_id": "7", "created_at": "2026-09-01T12:00:00Z", "read_at": null,
              "reason_flags": { "title": "Heat 2", "reason": "Not released yet", "media_type": "movie", "tmdb_id": 5 }
            }
          ],
          "page": { "has_more": true, "next_cursor": "c2" },
          "read_cutoff": "cut-1"
        }
        """;

    private const string SecondPage = """
        {
          "items": [ { "id": "n4", "type": "something.new", "profile_id": "7", "created_at": "2026-08-01T12:00:00Z", "read_at": null, "reason_flags": {} } ],
          "page": { "has_more": false },
          "read_cutoff": "cut-2"
        }
        """;

    private const string NoNotifications = """{ "items": [], "page": { "has_more": false }, "read_cutoff": "cut-0" }""";

    private readonly LibraryHarness _harness = new();
    private readonly NotificationBadge _badge;

    public NotificationsViewModelTests()
    {
        _harness.Answer(ListPath, FirstPage);
        _harness.Answer(CountPath, """{ "count": 2 }""");
        _harness.Answer(CapabilitiesPath, """{ "in_app": { "enabled": true }, "series": { "available": true, "mute": true } }""");
        _harness.Silo.On(HttpMethod.Post, "api/v2/notifications/n1/read", ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Post, "api/v2/notifications/read-all", ScriptedSilo.NoContent());
        _badge = new NotificationBadge(_harness.Session, Api(), _harness.Clock, _harness.Logs.For<NotificationBadge>());
    }

    private NotificationsApi Api() => new(_harness.Silo.Http);

    private NotificationsViewModel Create() =>
        new(_harness.Session, Api(), _badge, _harness.Navigation, _harness.Clock, _harness.Logs.For<NotificationsViewModel>());

    private async Task<NotificationsViewModel> LoadedAsync()
    {
        var inbox = Create();
        await inbox.ActivateAsync(isReturn: false);
        return inbox;
    }

    [Fact]
    public async Task TheInbox_ShowsEachEntryAsTheWebWordsIt()
    {
        var inbox = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, inbox.State);
        Assert.Equal(["Severance", "Dune: Part Three", "Heat 2"], inbox.Rows.Select(r => r.Title));
        Assert.Equal(["S2E1 — Hello, Ms. Cobel", "A movie you followed is now available", "Your request was declined — Not released yet"],
            inbox.Rows.Select(r => r.Description));
        Assert.Equal(["5m ago", "3d ago", "Sep 1"], inbox.Rows.Select(r => r.When));
        Assert.Equal(["Favorite", "Next Up"], inbox.Rows[0].Reasons);
        Assert.Equal([true, false, true], inbox.Rows.Select(r => r.IsUnread));
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/s.webp?sig=s"), inbox.Rows[0].PosterUrl);
        Assert.Equal("status=all&limit=25", _harness.Queries(ListPath)[0]);
    }

    [Fact]
    public async Task AnEntry_OpensItsTitle_AndIsReadFromThen()
    {
        var inbox = await LoadedAsync();

        inbox.OpenCommand.Execute(inbox.Rows[0]);
        inbox.OpenCommand.Execute(inbox.Rows[2]);

        Assert.Equal((AppRoute.Item, (object?)"episode:severance-s02e01"), Assert.Single(_harness.Navigation.Navigations));
        Assert.False(inbox.Rows[0].IsUnread);
        Assert.False(inbox.Rows[2].CanOpen);
        Assert.Equal(1, _badge.UnreadCount);
        await WaitUntilAsync(() => _harness.Silo.SentTo(HttpMethod.Post, "api/v2/notifications/n1/read").Count == 1);
    }

    [Fact]
    public async Task AReadMarkThatFails_ComesBackUnread()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/notifications/n1/read", ScriptedSilo.Problem(503, "unavailable"));
        var inbox = await LoadedAsync();

        inbox.MarkReadCommand.Execute(inbox.Rows[0]);

        await WaitUntilAsync(() => inbox.Rows[0].IsUnread);
        Assert.Equal("Failed to mark notification read", inbox.Notice);
        Assert.Equal(2, _badge.UnreadCount);
    }

    [Fact]
    public async Task LoadMore_AddsTheNextPage_UntilThereIsNone()
    {
        var inbox = await LoadedAsync();
        _harness.Answer(ListPath, SecondPage);

        await inbox.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal("status=all&limit=25&cursor=c2", _harness.Queries(ListPath)[^1]);
        Assert.Equal("Notification", inbox.Rows[^1].Title);
        Assert.Equal("something.new", inbox.Rows[^1].Description);
        Assert.False(inbox.CanLoadMore);
    }

    [Fact]
    public async Task MarkAllRead_MarksWhatTheFirstPageSaw_AndReadsTheInboxAgain()
    {
        var inbox = await LoadedAsync();
        _harness.Answer(CountPath, """{ "count": 0 }""");

        await inbox.MarkAllReadCommand.ExecuteAsync(null);

        var sent = Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/notifications/read-all"));
        Assert.Equal("cut-1", JsonDocument.Parse(sent.Body!).RootElement.GetProperty("through").GetString());
        Assert.Equal(2, _harness.Queries(ListPath).Count);
        Assert.False(inbox.HasUnread);
    }

    [Fact]
    public async Task AMarkAllThatFails_AsksForTheInboxToBeReadFirst()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/notifications/read-all", ScriptedSilo.Problem(409, "conflict"));
        var inbox = await LoadedAsync();

        await inbox.MarkAllReadCommand.ExecuteAsync(null);

        Assert.Equal(NotificationsViewModel.MarkAllFailedText, inbox.MarkAllError);
        Assert.False(inbox.MarkAllReadCommand.CanExecute(null));

        await inbox.LoadCommand.ExecuteAsync(null);
        Assert.True(inbox.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unread_ShowsOnlyUnreadEntries_AndCountsThem()
    {
        var inbox = await LoadedAsync();
        Assert.Equal(["All", "Unread (2)", "Series"], inbox.Filters.Select(f => f.Label));
        _harness.Answer(ListPath, NoNotifications);

        inbox.SelectedFilter = inbox.Filters[1];
        await WaitUntilAsync(() => inbox.State == LibraryLoadState.Empty);

        Assert.Equal("status=unread&limit=25", _harness.Queries(ListPath)[^1]);
        Assert.Equal("No unread notifications", inbox.EmptyTitle);
    }

    [Fact]
    public async Task ACountChange_KeepsTheFilter_WithoutReadingTheInboxAgain()
    {
        var inbox = await LoadedAsync();
        inbox.SelectedFilter = inbox.Filters[1];
        await WaitUntilAsync(() => inbox.State == LibraryLoadState.Ready && _harness.Queries(ListPath).Count == 2);

        _badge.Show(5);

        Assert.Equal("Unread (5)", inbox.SelectedFilter.Label);
        Assert.Equal(2, _harness.Queries(ListPath).Count);
    }

    [Fact]
    public async Task AReloadDuringLoadMore_LeavesLoadMoreWorking()
    {
        var inbox = await LoadedAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(ListPath, LibraryHarness.Held(release, SecondPage), ScriptedSilo.Json(HttpStatusCode.OK, FirstPage));
        var more = inbox.LoadMoreCommand.ExecuteAsync(null);

        await inbox.LoadCommand.ExecuteAsync(null);
        release.SetResult();
        await more;

        Assert.False(inbox.IsLoadingMore);
        Assert.True(inbox.LoadMoreCommand.CanExecute(null));
        Assert.Equal(3, inbox.Rows.Count);
    }

    [Fact]
    public async Task MarkAllRead_ReadsTheInboxAfterIt_EvenWhenTheFilterChangedMeanwhile()
    {
        var inbox = await LoadedAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Post, "api/v2/notifications/read-all", async () =>
        {
            await release.Task;
            return await ScriptedSilo.NoContent()();
        });
        var marking = inbox.MarkAllReadCommand.ExecuteAsync(null);
        inbox.SelectedFilter = inbox.Filters[1];
        await WaitUntilAsync(() => _harness.Queries(ListPath).Count == 2);

        release.SetResult();
        await marking;

        Assert.Equal(3, _harness.Queries(ListPath).Count);
        Assert.Equal("status=unread&limit=25", _harness.Queries(ListPath)[^1]);
        Assert.False(inbox.HasMarkAllFailed);
    }

    [Fact]
    public async Task ACountThatFails_LeavesTheList()
    {
        _harness.Answer(CountPath, ScriptedSilo.NetworkError());

        var inbox = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, inbox.State);
        Assert.Equal(3, inbox.Rows.Count);
    }

    [Fact]
    public async Task APageThatWent_NoLongerFollowsTheCount()
    {
        var inbox = await LoadedAsync();

        inbox.Deactivate();
        _badge.Show(7);
        Assert.Equal("Unread (2)", inbox.Filters[1].Label);

        await inbox.ActivateAsync(isReturn: true);
        Assert.Equal("Unread (7)", inbox.Filters[1].Label);
    }

    [Fact]
    public async Task TwoQuickSwitches_EndAsTheLatestSaveLeftThem()
    {
        const string PreferencesPath = "api/v2/notifications/preferences";
        _harness.Answer(PreferencesPath, """{ "enabled": true, "notify_favorites": false, "notify_watchlist": false, "notify_continue_watching": true, "notify_next_up": true, "profile_id": "7" }""");
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, PreferencesPath,
            LibraryHarness.Held(release, """{ "enabled": true, "notify_favorites": true, "notify_watchlist": false, "notify_continue_watching": true, "notify_next_up": true, "profile_id": "7" }"""),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "enabled": true, "notify_favorites": true, "notify_watchlist": true, "notify_continue_watching": true, "notify_next_up": true, "profile_id": "7" }"""));
        var preferences = Create().Preferences;
        await preferences.LoadAsync();

        preferences.NotifyFavorites = true;
        preferences.NotifyWatchlist = true;
        await WaitUntilAsync(() => _harness.Silo.SentTo(HttpMethod.Put, PreferencesPath).Count == 2);
        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(preferences.NotifyFavorites);
        Assert.True(preferences.NotifyWatchlist);
        Assert.Equal(["{\"notify_favorites\":true}", "{\"notify_watchlist\":true}"], _harness.Silo.SentTo(HttpMethod.Put, PreferencesPath).Select(r => r.Body));
    }

    [Fact]
    public async Task AnInboxThatFails_CanBeReadAgain()
    {
        _harness.Answer(ListPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, FirstPage));

        var inbox = await LoadedAsync();
        Assert.Equal(LibraryLoadState.Error, inbox.State);

        await inbox.LoadCommand.ExecuteAsync(null);
        Assert.Equal(3, inbox.Rows.Count);
    }

    [Fact]
    public async Task TheBadge_ReadsTheCountAtMostEveryHalfMinute()
    {
        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, _harness.Count(CountPath));
        Assert.Equal("2", _badge.CountText);

        _harness.Clock.Advance(NotificationBadge.FreshFor);
        _harness.Answer(CountPath, """{ "count": 120 }""");
        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("99+", _badge.CountText);
        Assert.Equal(1, _harness.Count(CapabilitiesPath));
    }

    [Fact]
    public async Task AnInboxTheServerTurnedOff_HidesTheEntry()
    {
        _harness.Answer(CapabilitiesPath, """{ "in_app": { "enabled": false } }""");

        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(_badge.IsAvailable);
        Assert.Equal(0, _harness.Count(CountPath));
    }

    [Fact]
    public async Task ACapabilityThatFails_HidesTheEntry_UntilItCanBeRead()
    {
        _harness.Answer(CapabilitiesPath, ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "in_app": { "enabled": true } }"""));

        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(_badge.IsAvailable);

        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(_badge.IsAvailable);
        Assert.Equal(2, _badge.UnreadCount);
    }

    [Fact]
    public async Task AnotherProfile_StartsFromNothing()
    {
        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        _harness.Session.ProfileId = "8";
        _harness.Answer(CountPath, """{ "count": 0 }""");

        await _badge.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _badge.UnreadCount);
        Assert.Equal(2, _harness.Count(CapabilitiesPath));
    }

    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(59, "59m ago")]
    [InlineData(90, "2h ago")]
    [InlineData(23 * 60, "23h ago")]
    [InlineData(6 * 24 * 60, "6d ago")]
    public void Times_ReadAsTheWebs(int minutesAgo, string text)
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(text, NotificationText.When(now.AddMinutes(-minutesAgo), now));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
