using Granary.Core.Realtime;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The scheduled tasks of the activity indicator, as the web keeps them: the whole list with the hidden workers is read
/// once the server gave the tasks channel and again on <c>task.updated</c>, which a running task sends four times a second.
/// </summary>
public sealed class ServerActivityTasksTests : IAsyncDisposable
{
    private const string TasksPath = "api/v2/admin/tasks";

    private const string Tasks = """
        {
          "items": [
            { "key": "refresh_metadata", "name": "Refresh Metadata", "description": "d", "category": "metadata", "state": "running",
              "progress": 41.5, "manual_only": false, "triggers": [], "execution_scope": "process" },
            { "key": "backup", "name": "Back Up Server", "description": "d", "category": "system", "state": "idle", "progress": 0,
              "manual_only": false, "triggers": [], "execution_scope": "process",
              "last_execution": { "task_key": "backup", "status": "completed", "duration_ms": 1200 } }
          ]
        }
        """;

    private const string NoneRunning = """
        { "items": [ { "key": "refresh_metadata", "name": "Refresh Metadata", "state": "idle", "progress": 0 } ] }
        """;

    private readonly ServerActivityHarness _harness = new();

    public ServerActivityTasksTests()
    {
        _harness.Library.Answer(TasksPath, Tasks);
    }

    private int Reads => _harness.Library.Count(TasksPath);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static string Updated(double progress) =>
        $$"""{ "type": "event", "channel": "tasks", "event": "task.updated", "event_id": "t1", "timestamp": "t", "data": { "key": "refresh_metadata", "state": "running", "progress": {{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }""";

    [Fact]
    public async Task TheTasks_AreReadWithTheHiddenOnes_OnceTheServerGaveTheirChannel()
    {
        await _harness.StartedAsync(EventsChannels.Tasks);

        await ServerPlaybackFixture.Until(() => _harness.State.Tasks is not null, "the tasks");
        var tasks = _harness.State.Tasks!;
        Assert.Equal(["refresh_metadata", "backup"], tasks.Select(task => task.Key));
        Assert.Equal(("Refresh Metadata", true, 41.5), (tasks[0].Name, tasks[0].IsRunning, tasks[0].Progress));
        Assert.False(tasks[1].IsRunning);
        Assert.Equal("include_hidden=true", Assert.Single(_harness.Library.Queries(TasksPath)));
        // The sessions' channel was not given: nothing of theirs is asked for.
        Assert.Empty(_harness.Library.Silo.SentTo(HttpMethod.Get, "api/v2/admin/sessions"));
    }

    [Fact]
    public async Task ARunningTasksNews_ReadsTheListAtOnce_AndThenOncePerWindowHoweverOftenItComes()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Tasks);
        await ServerPlaybackFixture.Until(() => _harness.State.Tasks is not null, "the tasks");
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        // Four a second for five seconds: the first reads at once.
        await _harness.PushedAsync(socket, Updated(42));
        await ServerPlaybackFixture.Until(() => Reads == 2 && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the read at once");
        for (int tick = 1; tick <= 20; tick++)
        {
            Assert.Equal(2, Reads);
            _harness.Library.Clock.Advance(TimeSpan.FromMilliseconds(250));
            await _harness.PushedAsync(socket, Updated(42 + tick));
        }
        await ServerPlaybackFixture.Until(() => Reads == 3, "the read after the window");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // One more when the window closed, whatever came meanwhile; the frame's own task is never shown.
        Assert.Equal(3, Reads);
        Assert.Equal(41.5, _harness.State.Tasks![0].Progress);
    }

    [Fact]
    public async Task ATaskThatEnded_IsGoneWithTheNextRead()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Tasks);
        await ServerPlaybackFixture.Until(() => _harness.State.Tasks?.Count == 2, "the tasks");
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        _harness.Library.Answer(TasksPath, NoneRunning);

        await _harness.PushedAsync(socket, """{ "type": "event", "channel": "tasks", "event": "task.updated", "event_id": "t2", "timestamp": "t", "data": { "key": "refresh_metadata", "state": "idle", "progress": 0 } }""");

        await ServerPlaybackFixture.Until(() => _harness.State.Tasks?.Count == 1, "the read");
        Assert.DoesNotContain(_harness.State.Tasks!, task => task.IsRunning);
    }

    [Fact]
    public async Task AServerWithoutTheTasksList_HasNoTasks_AndStillItsStreams()
    {
        _harness.Library.Answer(TasksPath, ScriptedSilo.Problem(404, "not_found"));
        _harness.Library.Answer("api/v2/admin/sessions", Admin.AdminPayloads.Sessions);

        await _harness.StartedAsync(EventsChannels.Sessions, EventsChannels.Tasks);

        await ServerPlaybackFixture.Until(() => _harness.State.Sessions?.Count == 2 && Reads == 1, "the reads");
        Assert.Null(_harness.State.Tasks);
    }
}
