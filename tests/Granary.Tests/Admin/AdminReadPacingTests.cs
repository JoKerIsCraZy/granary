using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>
/// How the admin pages pace their reads and commands: a read in flight is never restarted by the clock, a failed one waits
/// its interval, every page of sessions is read, and one stream's command never holds up another's.
/// </summary>
public sealed class AdminReadPacingTests
{
    private readonly BrowseFakeSilo _silo = AdminPayloads.Dashboard();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly TestClock _clock = new(AdminPayloads.Now);
    private AdminSidebarViewModel? _sidebar;

    private AdminSidebarViewModel Sidebar => _sidebar ??= new(_session, new AdminApi(_silo.Http), _clock, _silo.Logs.For<AdminSidebarViewModel>());

    private AdminActivityViewModel Activity() =>
        new(_session, new AdminApi(_silo.Http), Sidebar, new RecordingNavigation(), _clock, _silo.Logs.For<AdminActivityViewModel>());

    private AdminDashboardViewModel Dashboard() =>
        new(_session, new AdminApi(_silo.Http), Sidebar, new RecordingNavigation(), _clock, _silo.Logs.For<AdminDashboardViewModel>());

    [Fact]
    public async Task SlowRead_IsNotRestartedByTheClock()
    {
        var activity = Activity();
        await activity.ActivateAsync();
        var answer = new TaskCompletionSource();
        _silo.Map("api/v2/admin/sessions", async (_, _) =>
        {
            await answer.Task;
            return BrowseFakeSilo.Json(AdminPayloads.NoSessions);
        });

        _clock.Now = AdminPayloads.Now.AddSeconds(10);
        var read = activity.TickAsync();
        for (int second = 11; second < 30; second++)
        {
            _clock.Now = AdminPayloads.Now.AddSeconds(second);
            await activity.TickAsync();
        }
        answer.SetResult();
        await read;

        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
        Assert.Equal(LibraryLoadState.Empty, activity.State);
    }

    [Fact]
    public async Task FailedRead_WaitsItsInterval_BeforeTheNext()
    {
        var activity = Activity();
        await activity.ActivateAsync();
        _silo.Map("api/v2/admin/sessions", _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));

        for (int second = 10; second < 20; second++)
        {
            _clock.Now = AdminPayloads.Now.AddSeconds(second);
            await activity.TickAsync();
        }

        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
        // An automatic read that failed keeps the streams and says nothing.
        Assert.Equal(2, activity.Streams.Count);
        Assert.False(activity.HasNotice);
    }

    [Fact]
    public async Task Dashboard_ReadsAMinuteAfterTheLastReadBegan_NeverTwiceAtOnce()
    {
        var dashboard = Dashboard();
        await dashboard.ActivateAsync();
        var answer = new TaskCompletionSource();
        _silo.Map("api/v2/admin/stats", async (_, _) =>
        {
            await answer.Task;
            return BrowseFakeSilo.Json(AdminPayloads.Stats);
        });

        _clock.Now = AdminPayloads.Now.AddSeconds(60);
        var read = dashboard.TickAsync();
        _clock.Now = AdminPayloads.Now.AddSeconds(150);
        await dashboard.TickAsync();
        answer.SetResult();
        await read;

        Assert.Equal(2, _silo.To("api/v2/admin/stats").Count);
    }

    [Fact]
    public async Task Sessions_AreReadPageByPage()
    {
        _silo.Map("api/v2/admin/sessions", request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            return BrowseFakeSilo.Json(query["cursor"] is null
                ? """{ "items": [ { "session_id": "a", "user_id": "1", "username": "elena", "media_title": "Dune", "media_type": "movie", "play_method": "direct", "started_at": "2026-10-27T12:00:00.000Z", "updated_at": "2026-10-27T12:00:00.000Z", "reporting_node": "local", "stream_location": "local" } ], "page": { "has_more": true, "next_cursor": "p2" } }"""
                : """{ "items": [ { "session_id": "b", "user_id": "2", "username": "kim", "media_title": "Heat", "media_type": "movie", "play_method": "direct", "started_at": "2026-10-27T12:10:00.000Z", "updated_at": "2026-10-27T12:10:00.000Z", "reporting_node": "local", "stream_location": "local" } ], "page": { "has_more": false } }""");
        });
        var activity = Activity();

        await activity.ActivateAsync();

        var reads = _silo.To("api/v2/admin/sessions");
        Assert.Equal(("100", null, "p2"), (reads[0].Query("limit"), reads[0].Query("cursor"), reads[1].Query("cursor")));
        Assert.Equal(["b", "a"], activity.Streams.Select(row => row.SessionId));
        Assert.Equal("2 live", Sidebar.LiveText);
    }

    [Fact]
    public async Task Sessions_WithARepeatedCursor_FailTheRead()
    {
        _silo.Map("api/v2/admin/sessions", """{ "items": [], "page": { "has_more": true, "next_cursor": "same" } }""");
        var activity = Activity();

        await activity.ActivateAsync();

        Assert.Equal(LibraryLoadState.Error, activity.State);
        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
    }

    [Fact]
    public async Task OneStreamsCommand_DoesNotHoldUpAnothers_AndAStreamWaitsForItsOwn()
    {
        var stopA = new TaskCompletionSource();
        _silo.Map("api/v2/admin/sessions/s1/stop", async (request, _) =>
        {
            await stopA.Task;
            return Receipt(request);
        });
        _silo.Map("api/v2/admin/sessions/s2/stop", (request, _) => Task.FromResult(Receipt(request)));
        var activity = Activity();
        await activity.ActivateAsync();
        var episode = activity.Streams[0];
        var movie = activity.Streams[1];

        var first = activity.StopCommand.ExecuteAsync(episode);
        await activity.StopCommand.ExecuteAsync(movie);
        Assert.Single(_silo.To("api/v2/admin/sessions/s2/stop"));

        await activity.StopCommand.ExecuteAsync(episode);
        Assert.Equal("A command for this stream is still on its way. Try again in a moment.", activity.Notice);
        stopA.SetResult();
        await first;

        Assert.Single(_silo.To("api/v2/admin/sessions/s1/stop"));
        Assert.Equal("Stop command sent", activity.Notice);
    }

    [Fact]
    public async Task APauseThePlayerDidNotTake_GivesWayToTheServersWord()
    {
        _silo.Map("api/v2/admin/sessions/s1/pause", request => Receipt(request));
        var activity = Activity();
        await activity.ActivateAsync();
        var episode = activity.Streams[0];

        await activity.TogglePauseCommand.ExecuteAsync(episode);
        Assert.True(episode.IsPaused);

        _clock.Now = AdminPayloads.Now.AddSeconds(10);
        await activity.TickAsync();
        Assert.True(episode.IsPaused);

        _clock.Now = AdminPayloads.Now.AddSeconds(20);
        await activity.TickAsync();
        Assert.False(episode.IsPaused);
        Assert.Equal("Pause", episode.PauseLabel);
    }

    private static HttpResponseMessage Receipt(HttpRequestMessage request)
    {
        string id = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("command_id").GetString()!;
        return BrowseFakeSilo.Json($$"""{ "command_id": "{{id}}", "delivery": "dispatched", "outcome": "applied", "sequence": 1 }""");
    }
}
