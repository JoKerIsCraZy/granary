using System.Globalization;
using Granary.Core.Realtime;
using Granary.Tests.Admin;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The library scans of the activity indicator, as the web keeps them: they come with the stream alone, the channel's
/// snapshot replacing what is known and each <c>scan.*</c> event bringing one run.
/// </summary>
public sealed class ServerActivityScansTests : IAsyncDisposable
{
    private const string LibrariesPath = "api/v2/libraries";

    private readonly ServerActivityHarness _harness = new();

    public ServerActivityScansTests()
    {
        _harness.Library.Answer(LibrariesPath, AdminPayloads.Libraries);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static string Run(string id, string status, long library = 1, string mode = "library", string? path = null, string result = "") =>
        $$"""{ "id": "{{id}}", "library_id": {{library.ToString(CultureInfo.InvariantCulture)}}, "mode": "{{mode}}", {{(path is null ? "" : $"\"path\": \"{path}\", ")}}"trigger": "manual", "status": "{{status}}"{{(result.Length == 0 ? "" : $", \"result\": {result}")}} }""";

    private static string Snapshot(params string[] runs) =>
        $$"""{ "type": "snapshot", "channel": "scans", "timestamp": "t", "data": [{{string.Join(", ", runs)}}] }""";

    private static string Event(string name, string run) =>
        $$"""{ "type": "event", "channel": "scans", "event": "{{name}}", "event_id": "{{name}}", "timestamp": "t", "data": {{run}} }""";

    private IEnumerable<(string Id, string Status)> Scans => _harness.State.Scans!.Select(scan => (scan.Id, scan.Status));

    [Fact]
    public async Task UntilTheStreamSaysSo_TheScansAreNotKnown()
    {
        await _harness.StartedAsync(EventsChannels.Scans);

        Assert.Null(_harness.State.Scans);
    }

    [Fact]
    public async Task TheSnapshot_IsTheListOfScans()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);

        await _harness.PushedAsync(socket, Snapshot(
            Run("a", "running", mode: "subtree", path: "/media/tv/Dark", result: """{ "new": 2, "message": "Matching files", "total_files": 4800, "files_processed": 1200 }"""),
            Run("b", "accepted", library: 2)));

        Assert.Equal([("a", "running"), ("b", "accepted")], Scans);
        var first = _harness.State.Scans![0];
        Assert.Equal((1L, "subtree", "/media/tv/Dark", true, true), (first.LibraryId, first.Mode, first.Path, first.IsRunning, first.IsActive));
        Assert.Equal(("Matching files", 4800L, 1200L), (first.Result!.Message, first.Result.TotalFiles, first.Result.FilesProcessed));
        Assert.Null(_harness.State.Scans[1].Result);
    }

    [Fact]
    public async Task AnEmptySnapshot_SaysThereAreNone()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);

        await _harness.PushedAsync(socket, Snapshot());

        Assert.Empty(_harness.State.Scans!);
    }

    [Fact]
    public async Task EachEvent_BringsOneRun_WhichJoinsChangesOrLeaves()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);
        await _harness.PushedAsync(socket, Snapshot(Run("a", "running")));

        await _harness.PushedAsync(socket, Event("scan.accepted", Run("b", "accepted", library: 2)));
        Assert.Equal([("a", "running"), ("b", "accepted")], Scans);

        await _harness.PushedAsync(socket, Event("scan.progress", Run("a", "running", result: """{ "message": "Reading files", "total_files": 10, "files_processed": 4 }""")));
        Assert.Equal(4, _harness.State.Scans![0].Result!.FilesProcessed);

        await _harness.PushedAsync(socket, Event("scan.completed", Run("a", "completed")));
        await _harness.PushedAsync(socket, Event("scan.started", Run("b", "running", library: 2)));
        Assert.Equal([("b", "running")], Scans);

        await _harness.PushedAsync(socket, Event("scan.failed", Run("b", "failed", library: 2)));
        await _harness.PushedAsync(socket, Event("scan.cancelled", Run("c", "cancelled")));
        Assert.Empty(_harness.State.Scans!);
    }

    [Fact]
    public async Task AnEventBeforeAnySnapshot_MakesTheListKnown()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);

        await _harness.PushedAsync(socket, Event("scan.completed", Run("z", "completed")));
        Assert.Empty(_harness.State.Scans!);

        await _harness.PushedAsync(socket, Event("scan.accepted", Run("a", "accepted")));
        Assert.Equal([("a", "accepted")], Scans);
    }

    [Fact]
    public async Task ALaterConnectionsSnapshot_ReplacesWhatWasKnown_WhichStaysUntilThen()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);
        await _harness.PushedAsync(socket, Snapshot(Run("a", "running"), Run("b", "accepted")));

        socket.Close();
        await ServerPlaybackFixture.Until(() => _harness.Library.Clock.NextDue == TimeSpan.FromSeconds(1), "the wait before the next connection");
        _harness.Library.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = await _harness.OpenedAsync(1, EventsChannels.Scans);
        Assert.Equal(2, _harness.State.Scans!.Count);

        // "a" ended while the stream was away.
        await _harness.PushedAsync(next, Snapshot(Run("b", "running")));
        Assert.Equal([("b", "running")], Scans);
    }

    [Fact]
    public async Task WhatIsNoRun_ChangesNothing()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);
        await _harness.PushedAsync(socket, Snapshot(Run("a", "running")));

        await _harness.PushedAsync(socket, """{ "type": "event", "channel": "scans", "event": "scan.progress", "event_id": "x", "timestamp": "t", "data": "soon" }""");
        await _harness.PushedAsync(socket, """{ "type": "event", "channel": "scans", "event": "scan.progress", "event_id": "y", "timestamp": "t", "data": { "id": 7 } }""");
        await _harness.PushedAsync(socket, """{ "type": "snapshot", "channel": "scans", "timestamp": "t", "data": { "runs": [] } }""");
        // A snapshot too large to take is cut to its head and says nothing of its runs.
        await _harness.PushedAsync(socket, """{"type":"snapshot","channel":"scans","timestamp":"t","data":[{"id":"q","library_id":1,"mode":"lib""");

        Assert.Equal([("a", "running")], Scans);
    }

    [Fact]
    public async Task WithoutTheirChannel_ScansAreNeitherKeptNorNamed()
    {
        _harness.Library.Answer("api/v2/admin/sessions", AdminPayloads.NoSessions);
        var socket = await _harness.StartedAsync(EventsChannels.Sessions);

        await _harness.PushedAsync(socket, Snapshot(Run("a", "running")));
        await _harness.PushedAsync(socket, Event("scan.accepted", Run("b", "accepted")));

        Assert.Null(_harness.State.Scans);
        Assert.Null(_harness.State.LibraryNames);
        Assert.Equal(0, _harness.Library.Count(LibrariesPath));
    }

    [Fact]
    public async Task TheLibrariesNames_AreReadForTheRows()
    {
        await _harness.StartedAsync(EventsChannels.Scans);

        await ServerPlaybackFixture.Until(() => _harness.State.LibraryNames is not null, "the libraries");
        Assert.Equal("Filme", _harness.State.LibraryNames!["1"]);
        Assert.Equal(1, _harness.Library.Count(LibrariesPath));
    }

    [Fact]
    public async Task AScanOfALibraryNobodyKnows_AsksForTheNamesOnce()
    {
        var socket = await _harness.StartedAsync(EventsChannels.Scans);
        await ServerPlaybackFixture.Until(() => _harness.State.LibraryNames is not null, "the libraries");
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);

        // Library 99 was made after the names were read; the server's list still does not have it.
        await _harness.PushedAsync(socket, Event("scan.accepted", Run("n", "accepted", library: 99)));
        await ServerPlaybackFixture.Until(() => _harness.Library.Count(LibrariesPath) == 2
            && _harness.Library.Clock.NextDue == ServerActivity.RefreshWindow, "the names read again");
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await _harness.PushedAsync(socket, Event("scan.started", Run("n", "running", library: 99)));
        await _harness.PushedAsync(socket, Event("scan.progress", Run("n", "running", library: 99)));
        _harness.Library.Clock.Advance(ServerActivity.RefreshWindow);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, _harness.Library.Count(LibrariesPath));
    }

    [Fact]
    public async Task AServerWithoutTheLibraryList_StillShowsItsScans()
    {
        _harness.Library.Answer(LibrariesPath, ScriptedSilo.Problem(403, "forbidden"));
        var socket = await _harness.StartedAsync(EventsChannels.Scans);

        await _harness.PushedAsync(socket, Snapshot(Run("a", "running")));
        await ServerPlaybackFixture.Until(() => _harness.Library.Count(LibrariesPath) == 1, "the read");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([("a", "running")], Scans);
        Assert.Null(_harness.State.LibraryNames);
    }
}
