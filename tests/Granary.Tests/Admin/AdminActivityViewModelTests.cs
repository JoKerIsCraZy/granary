using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Activity: the streams, their shares and filters, the address lookup, and the commands a stream takes.</summary>
public sealed class AdminActivityViewModelTests
{
    private const string Receipt = """{ "command_id": "{0}", "delivery": "dispatched", "outcome": "applied", "sequence": 1 }""";

    private readonly BrowseFakeSilo _silo = AdminPayloads.Dashboard();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly UtcClock _clock = new(AdminPayloads.Now);
    private AdminSidebarViewModel? _sidebar;

    private AdminSidebarViewModel Sidebar => _sidebar ??= new(_session, new AdminApi(_silo.Http), _clock, _silo.Logs.For<AdminSidebarViewModel>());

    private AdminActivityViewModel Create() =>
        new(_session, new AdminApi(_silo.Http), Sidebar, _navigation, _clock, _silo.Logs.For<AdminActivityViewModel>());

    private async Task<AdminActivityViewModel> ShownAsync()
    {
        var activity = Create();
        await activity.ActivateAsync();
        return activity;
    }

    [Fact]
    public async Task Activate_ListsTheStreams_NewestFirst()
    {
        var activity = await ShownAsync();

        Assert.Equal(LibraryLoadState.Ready, activity.State);
        Assert.Equal(["s1", "s2"], activity.Streams.Select(row => row.SessionId));
        Assert.Equal("2 active streams across 1 node", activity.SubtitleText);
        var episode = activity.Streams[0];
        Assert.Equal(("Past and Present", "S1E3 · Dark", "elena", "Elena", "Direct Stream"),
            (episode.Title, episode.Subtitle, episode.Username, episode.Profile, episode.MethodLabel));
        Assert.Equal(("10:00 / 50:00", 0.2, "8.0 Mbps", "Node Local server", "Local"),
            (episode.Position, episode.Progress, episode.Bitrate, episode.Route, episode.Location));
        Assert.Equal(("5m ago", "5:00"), (episode.Started, episode.Elapsed));
        Assert.Equal("2 live", Sidebar.LiveText);
    }

    [Fact]
    public async Task Shares_CountEachMethod_InTheWebsOrder()
    {
        var activity = await ShownAsync();

        Assert.Equal(
        [
            new AdminMethodShare("direct", 1, 0.5, false, false),
            new AdminMethodShare("direct_stream", 1, 0.5, false, false),
        ], activity.Methods);
        Assert.False(activity.HasNodeChoice);
    }

    [Fact]
    public async Task MethodFilter_NarrowsTheStreams_AndDimsTheOthers()
    {
        var activity = await ShownAsync();

        activity.ToggleMethodCommand.Execute(activity.Methods[0]);

        Assert.Equal(["s2"], activity.Streams.Select(row => row.SessionId));
        Assert.Equal("Showing 1 of 2 streams", activity.FilterStatusText);
        Assert.True(activity.Methods[0].IsSelected);
        Assert.True(activity.Methods[1].IsDimmed);
        activity.ToggleMethodCommand.Execute(activity.Methods[0]);
        Assert.Equal(2, activity.Streams.Count);
        Assert.Equal("", activity.FilterStatusText);
    }

    [Fact]
    public async Task SearchAndType_Filter_AndClearFiltersKeepsTheSearch()
    {
        var activity = await ShownAsync();

        activity.SearchText = "android";
        Assert.Equal(["s2"], activity.Streams.Select(row => row.SessionId));

        activity.SearchText = "";
        activity.ToggleMoviesCommand.Execute(null);
        Assert.True(activity.IsMoviesOnly);
        Assert.Equal(["s2"], activity.Streams.Select(row => row.SessionId));
        activity.ToggleSeriesCommand.Execute(null);
        Assert.True(activity.IsSeriesOnly);
        // The episode's media type is its own; the web's Series filter matches "series" alone.
        Assert.Empty(activity.Streams);
        Assert.True(activity.HasNoStreamsShown);
        Assert.Equal("No streams match your filters", activity.EmptyText);

        activity.SearchText = "dark";
        activity.ClearFiltersCommand.Execute(null);
        Assert.Equal("dark", activity.SearchText);
        Assert.Equal(["s1"], activity.Streams.Select(row => row.SessionId));
        Assert.False(activity.HasFilters);
    }

    [Fact]
    public async Task ReadAgain_KeepsTheRows_AndDropsEndedSessions()
    {
        var activity = await ShownAsync();
        var episode = activity.Streams[0];
        episode.IsExpanded = true;

        _silo.Map("api/v2/admin/sessions", """{ "items": [ { "session_id": "s1", "user_id": "1", "username": "elena", "media_title": "Secrets", "media_type": "episode", "play_method": "remux", "is_paused": true, "position_seconds": 900, "started_at": "2026-10-27T12:25:00.000Z", "updated_at": "2026-10-27T12:30:00.000Z", "reporting_node": "local", "stream_location": "local", "has_playback_control": true } ] }""");
        _clock.Now = AdminPayloads.Now.AddSeconds(10);
        await activity.TickAsync();

        Assert.Same(episode, Assert.Single(activity.Streams));
        Assert.True(episode.IsExpanded);
        Assert.True(episode.IsPaused);
        Assert.Equal("Resume", episode.PauseLabel);
        Assert.Equal("1 active stream across 1 node", activity.SubtitleText);
    }

    [Fact]
    public async Task Tick_ReadsAgainEveryTenSeconds()
    {
        var activity = await ShownAsync();

        _clock.Now = AdminPayloads.Now.AddSeconds(9);
        await activity.TickAsync();
        Assert.Single(_silo.To("api/v2/admin/sessions"));
        Assert.Equal("5:09", activity.Streams[0].Elapsed);

        _clock.Now = AdminPayloads.Now.AddSeconds(10);
        await activity.TickAsync();
        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
    }

    [Fact]
    public async Task NothingPlaying_IsEmpty()
    {
        _silo.Map("api/v2/admin/sessions", AdminPayloads.NoSessions);

        var activity = await ShownAsync();

        Assert.Equal(LibraryLoadState.Empty, activity.State);
        Assert.Equal("No active streams", activity.SubtitleText);
        Assert.Equal("No active streams", activity.EmptyText);
        Assert.True(activity.HasNoStreamsShown);
    }

    [Fact]
    public async Task FailedFirstRead_ShowsWhy_AFailedRefreshKeepsTheStreams()
    {
        _silo.Map("api/v2/admin/sessions", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var failed = await ShownAsync();
        Assert.Equal(LibraryLoadState.Error, failed.State);
        Assert.Equal(LibraryErrors.Busy, failed.ErrorText);

        _silo.Map("api/v2/admin/sessions", AdminPayloads.Sessions);
        var activity = await ShownAsync();
        _silo.Map("api/v2/admin/sessions", _ => throw new HttpRequestException("down"));
        await activity.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, activity.Streams.Count);
        Assert.True(activity.NoticeIsError);
        Assert.Equal(LibraryErrors.Unreachable, activity.Notice);
    }

    [Fact]
    public async Task Pause_SendsASequencedCommand_AndShowsItAtOnce()
    {
        var bodies = new List<JsonElement>();
        _silo.Map("api/v2/admin/sessions/s1/pause", request => Answer(request, bodies));
        var activity = await ShownAsync();
        var episode = activity.Streams[0];

        await activity.TogglePauseCommand.ExecuteAsync(episode);

        var body = Assert.Single(bodies);
        Assert.True(Guid.TryParse(body.GetProperty("command_id").GetString(), out _));
        Assert.Equal(AdminPayloads.Now.ToUnixTimeMilliseconds(), body.GetProperty("sequence").GetInt64());
        Assert.False(body.TryGetProperty("message", out _));
        Assert.True(episode.IsPaused);
        Assert.Equal("Pause command sent", activity.Notice);
        Assert.False(episode.IsBusy);
    }

    [Fact]
    public async Task Commands_ForOneSession_RiseInSequence()
    {
        var bodies = new List<JsonElement>();
        _silo.Map("api/v2/admin/sessions/s1/pause", request => Answer(request, bodies));
        _silo.Map("api/v2/admin/sessions/s1/resume", request => Answer(request, bodies));
        var activity = await ShownAsync();
        var episode = activity.Streams[0];

        await activity.TogglePauseCommand.ExecuteAsync(episode);
        await activity.TogglePauseCommand.ExecuteAsync(episode);

        Assert.Equal(bodies[0].GetProperty("sequence").GetInt64() + 1, bodies[1].GetProperty("sequence").GetInt64());
        Assert.False(episode.IsPaused);
        Assert.Equal("Resume command sent", activity.Notice);
    }

    [Fact]
    public async Task Pause_WithoutLiveControl_SendsNothing()
    {
        var activity = await ShownAsync();
        var movie = activity.Streams[1];

        await activity.TogglePauseCommand.ExecuteAsync(movie);
        Assert.False(await activity.SendMessageAsync(movie, "Hi"));

        Assert.True(movie.CannotControl);
        Assert.DoesNotContain(_silo.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Stop_WithoutALane_SaysTheServerEndsIt_AndReadsAgain()
    {
        _silo.Map("api/v2/admin/sessions/s2/stop", request =>
        {
            string id = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("command_id").GetString()!;
            return BrowseFakeSilo.Json($$"""{ "command_id": "{{id}}", "delivery": "fallback_scheduled", "outcome": "applied", "sequence": 9 }""");
        });
        var activity = await ShownAsync();

        await activity.StopCommand.ExecuteAsync(activity.Streams[1]);

        Assert.Equal("Stop could not reach the player directly. Silo will end the session shortly instead.", activity.Notice);
        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
    }

    [Fact]
    public async Task Message_IsTrimmed_AndRequired()
    {
        var bodies = new List<JsonElement>();
        _silo.Map("api/v2/admin/sessions/s1/message", request => Answer(request, bodies));
        var activity = await ShownAsync();

        Assert.False(await activity.SendMessageAsync(activity.Streams[0], "   "));
        Assert.Equal("Message is required", activity.Notice);
        Assert.True(await activity.SendMessageAsync(activity.Streams[0], "  Server restarts at 22:00  "));

        Assert.Equal("Server restarts at 22:00", Assert.Single(bodies).GetProperty("message").GetString());
        Assert.Equal("Message sent", activity.Notice);
    }

    [Theory]
    [InlineData(409, "conflict", "The stop command was not applied: Silo internal detail")]
    [InlineData(404, "not_found", "This playback session has already ended.")]
    [InlineData(503, "dependency_unavailable", "Playback control is unavailable on this server.")]
    [InlineData(500, "internal_error", "Failed to send session command")]
    public async Task FailedCommand_SaysWhy(int status, string code, string expected)
    {
        _silo.Map("api/v2/admin/sessions/s1/stop", _ => BrowseFakeSilo.Problem(status, code));
        var activity = await ShownAsync();

        await activity.StopCommand.ExecuteAsync(activity.Streams[0]);

        Assert.Equal(expected, activity.Notice);
        Assert.True(activity.NoticeIsError);
        Assert.False(activity.Streams[0].IsBusy);
    }

    [Fact]
    public async Task Terminate_ReportsBothFacts_AndReadsAgain()
    {
        string? body = null;
        _silo.Map("api/v2/admin/sessions/s2/terminate", request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return BrowseFakeSilo.Json("""{ "session_id": "s2", "authority_revoked": true, "already_revoked": false, "client_notified": false, "delivery": "unavailable", "durable_state": "stopped" }""");
        });
        var activity = await ShownAsync();

        await activity.TerminateCommand.ExecuteAsync(activity.Streams[1]);

        Assert.Equal("{}", body);
        Assert.Equal("Playback authority revoked; the player could not be reached and will stop when it next contacts the server.", activity.Notice);
        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
    }

    [Fact]
    public async Task IpLookup_ListsTheAccounts_AndLoadsMore()
    {
        _silo.Map("api/v2/admin/ips", request => BrowseFakeSilo.Json(BrowseRequestQuery(request, "cursor") is null
            ? """{ "items": [ { "user_id": "1", "username": "elena", "first_seen": "2026-10-01T08:00:00.000Z", "last_seen": "2026-10-27T12:00:00.000Z", "request_count": 12480 } ], "page": { "has_more": true, "next_cursor": "c2" } }"""
            : """{ "items": [ { "user_id": "2", "username": "", "first_seen": "2026-10-02T08:00:00.000Z", "last_seen": "2026-10-03T08:00:00.000Z", "request_count": 3 } ], "page": { "has_more": false } }"""));
        var activity = await ShownAsync();
        Assert.False(activity.LookupCommand.CanExecute(null));

        activity.IpQuery = " 203.0.113.50 ";
        await activity.LookupCommand.ExecuteAsync(null);

        var request = Assert.Single(_silo.To("api/v2/admin/ips"));
        Assert.Equal(("203.0.113.50", "30", "50"), (request.Query("ip"), request.Query("days"), request.Query("limit")));
        Assert.Equal(new AdminIpUserRow("elena", "Oct 1, 2026, 8:00 AM", "Oct 27, 2026, 12:00 PM", "12,480"), Assert.Single(activity.IpUsers));
        Assert.True(activity.HasMoreIpUsers);

        await activity.LoadMoreIpUsersCommand.ExecuteAsync(null);

        Assert.Equal("c2", _silo.To("api/v2/admin/ips")[^1].Query("cursor"));
        Assert.Equal("User #2", activity.IpUsers[1].Username);
        Assert.False(activity.HasMoreIpUsers);
        Assert.Equal("", activity.IpMessage);
    }

    [Fact]
    public async Task IpLookup_WithoutAccounts_OrFailing_SaysSo()
    {
        _silo.Map("api/v2/admin/ips", """{ "items": [], "page": { "has_more": false } }""");
        var activity = await ShownAsync();
        activity.IpQuery = "10.0.0.9";

        await activity.LookupCommand.ExecuteAsync(null);
        Assert.Equal("No users found for 10.0.0.9 in the last 30 days.", activity.IpMessage);

        _silo.Map("api/v2/admin/ips", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        await activity.LookupCommand.ExecuteAsync(null);
        Assert.Equal("Could not load IP history.", activity.IpMessage);
        Assert.True(activity.IpFailed);
    }

    [Fact]
    public async Task OpenTitle_OpensTheItem()
    {
        var activity = await ShownAsync();

        activity.OpenTitleCommand.Execute(activity.Streams[0].ContentId);

        Assert.Equal((AppRoute.Item, (object?)"episode:dark-s01e03"), Assert.Single(_navigation.Navigations));
    }

    // The viewer's zone is UTC here, so the lookup's times read the same on every machine.
    private sealed class UtcClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static string? BrowseRequestQuery(HttpRequestMessage request, string name) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[name];

    // A receipt for the command the request carried, its body kept.
    private static HttpResponseMessage Answer(HttpRequestMessage request, List<JsonElement> bodies)
    {
        var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.Clone();
        bodies.Add(body);
        return BrowseFakeSilo.Json(Receipt.Replace("{0}", body.GetProperty("command_id").GetString(), StringComparison.Ordinal));
    }
}
