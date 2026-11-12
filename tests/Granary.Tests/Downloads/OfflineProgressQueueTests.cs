using Granary.Core.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>The positions recorded on this PC that the server has not taken yet: one per title and profile, kept across starts.</summary>
public sealed class OfflineProgressQueueTests : IAsyncDisposable
{
    private readonly List<OfflineProgressQueue> _opened = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_folder, "offline-progress.json");

    private OfflineProgressQueue Open()
    {
        var queue = new OfflineProgressQueue(FilePath, _time, _logs.For<OfflineProgressQueue>());
        _opened.Add(queue);
        return queue;
    }

    public async ValueTask DisposeAsync()
    {
        // The file follows each change off the test's thread.
        foreach (var queue in _opened)
        {
            await queue.LatestSave;
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ARecord_KeepsThePositionAndWhenItWasRecorded_InWholeMilliseconds()
    {
        var queue = Open();
        _time.Advance(TimeSpan.FromTicks(12_345_678));

        var entry = queue.Record(Scope7, "ep_5", 1_830_500, 7_200_000);

        Assert.Equal(new OfflineProgressEntry(Scope7, "ep_5", 1_830_500, 7_200_000, ManualTimeProvider.DefaultStart.AddMilliseconds(1234),
            OfflineProgressState.Pending), entry);
        Assert.Equal([entry], queue.Entries(Scope7));
        Assert.True(queue.HasUnsent(Scope7, "ep_5"));
        Assert.False(queue.HasUnsent(Scope7, "ep_6"));
        Assert.False(queue.HasUnsent(Scope8, "ep_5"));
    }

    [Fact]
    public void ANewerSample_ReplacesTheItemsEntry()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 10_000, 7_200_000);
        queue.Record(Scope8, "ep_5", 99_000, 7_200_000);
        _time.Advance(TimeSpan.FromSeconds(10));

        queue.Record(Scope7, "ep_5", 20_000, 7_200_000);
        queue.Record(Scope7, "mv_1", 5_000, 0);

        Assert.Equal([("ep_5", 20_000L), ("mv_1", 5_000L)], queue.Entries(Scope7).Select(e => (e.MediaItemId, e.PositionMs)));
        // Each profile's own.
        Assert.Equal(99_000, Assert.Single(queue.Entries(Scope8)).PositionMs);
    }

    [Fact]
    public async Task TheQueue_SurvivesARestart()
    {
        var queue = Open();
        var entry = queue.Record(Scope7, "ep_5", 1_830_500, 7_200_000);
        await queue.LatestSave;

        Assert.Equal([entry], Open().Entries(Scope7));
        Assert.Contains("\"position_ms\": 1830500", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void AClaim_TakesThePendingEntries_OldestFirst_UpToTheMost()
    {
        var queue = Open();
        foreach (int n in Enumerable.Range(1, 5))
        {
            queue.Record(Scope7, "ep_" + n, n * 1000, 0);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var batch = queue.Claim(Scope7, 3);

        Assert.Equal(["ep_1", "ep_2", "ep_3"], batch.Select(e => e.MediaItemId));
        Assert.All(batch, e => Assert.Equal(OfflineProgressState.Sending, e.State));
        // What is on its way is not taken again.
        Assert.Equal(["ep_4", "ep_5"], queue.Claim(Scope7, 3).Select(e => e.MediaItemId));
        Assert.Empty(queue.Claim(Scope7, 3));
        Assert.Empty(queue.Claim(Scope8, 3));
    }

    [Fact]
    public void AnAnsweredBatch_LeavesTheQueue_ARefusedOneWaits_AnUncertainOneIsHeld()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_1", 1000, 0);
        queue.Record(Scope7, "ep_2", 2000, 0);
        queue.Record(Scope7, "ep_3", 3000, 0);
        var batch = queue.Claim(Scope7, 10);

        queue.Resolve([batch[0]], OfflineProgressOutcome.Done);
        queue.Resolve([batch[1]], OfflineProgressOutcome.Later);
        queue.Resolve([batch[2]], OfflineProgressOutcome.Uncertain);

        Assert.Equal([("ep_2", OfflineProgressState.Pending), ("ep_3", OfflineProgressState.Held)],
            queue.Entries(Scope7).Select(e => (e.MediaItemId, e.State)));
        // A held entry is never sent again by itself.
        Assert.Equal(["ep_2"], queue.Claim(Scope7, 10).Select(e => e.MediaItemId));
        Assert.True(queue.HasUnsent(Scope7, "ep_3"));
    }

    [Fact]
    public void ASampleRecordedWhileItsOlderOneIsOnItsWay_IsNotTouchedByThatOnesAnswer()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 10_000, 0);
        var batch = queue.Claim(Scope7, 10);
        _time.Advance(TimeSpan.FromSeconds(10));
        var newer = queue.Record(Scope7, "ep_5", 20_000, 0);

        queue.Resolve(batch, OfflineProgressOutcome.Done);

        Assert.Equal([newer], queue.Entries(Scope7));
        Assert.Equal([newer with { State = OfflineProgressState.Sending }], queue.Claim(Scope7, 10));
    }

    [Fact]
    public void ANewerSample_ReplacesAHeldOne_AndIsSent()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 10_000, 0);
        queue.Resolve(queue.Claim(Scope7, 10), OfflineProgressOutcome.Uncertain);
        _time.Advance(TimeSpan.FromSeconds(10));

        queue.Record(Scope7, "ep_5", 20_000, 0);

        Assert.Equal(20_000, Assert.Single(queue.Claim(Scope7, 10)).PositionMs);
    }

    [Fact]
    public void DropHeld_TakesOnlyTheHeldEntriesOfThatProfile()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_1", 1000, 0);
        queue.Record(Scope8, "ep_1", 1000, 0);
        queue.Resolve(queue.Claim(Scope7, 10), OfflineProgressOutcome.Uncertain);
        queue.Resolve(queue.Claim(Scope8, 10), OfflineProgressOutcome.Uncertain);
        queue.Record(Scope7, "ep_2", 2000, 0);

        queue.DropHeld(Scope7);

        Assert.Equal(["ep_2"], queue.Entries(Scope7).Select(e => e.MediaItemId));
        Assert.Single(queue.Entries(Scope8));
    }

    [Fact]
    public async Task AnEntryThatWasOnItsWayWhenGranaryClosed_IsHeldAtTheNextStart()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 10_000, 0);
        queue.Claim(Scope7, 10);
        await queue.LatestSave;

        var entry = Assert.Single(Open().Entries(Scope7));

        // The server may have taken it: it is not sent a second time.
        Assert.Equal(OfflineProgressState.Held, entry.State);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "entries": [ { "media_item_id": "ep_5" }, null, { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "", "position_ms": 5 } ] }""")]
    [InlineData("""{ "entries": [ { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "ep_5", "position_ms": -4, "duration_ms": 0, "updated_at": "2026-10-01T12:00:00+00:00", "state": "Pending" } ] }""")]
    public void AFileThatSaysNothingUsable_IsAnEmptyQueue_AndTheNextRecordIsKept(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);
        var queue = Open();

        Assert.Empty(queue.Entries(Scope7));
        queue.Record(Scope7, "ep_5", 10_000, 0);

        Assert.Single(queue.Entries(Scope7));
    }

    [Fact]
    public async Task EntriesNobodyCouldSendForNinetyDays_AreLetGo()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_old", 10_000, 0);
        _time.Advance(TimeSpan.FromDays(91));
        queue.Record(Scope7, "ep_new", 10_000, 0);
        await queue.LatestSave;

        // A server that was removed, or a profile that never came back: its entries could never go.
        Assert.Equal(["ep_new"], Open().Entries(Scope7).Select(e => e.MediaItemId));
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_KeepsTheQueueForThisRun_AndSaysSoWithoutThePath()
    {
        // A folder where the file should be: the write fails.
        Directory.CreateDirectory(FilePath);
        var queue = Open();

        queue.Record(Scope7, "ep_5", 10_000, 0);
        await queue.LatestSave;

        Assert.Single(queue.Entries(Scope7));
        Assert.Contains("could not be written", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
    }
}
