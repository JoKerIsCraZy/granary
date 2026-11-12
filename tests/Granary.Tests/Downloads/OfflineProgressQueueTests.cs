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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
        Assert.Contains("\"position_ms\": 1830500", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
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
    public void AnAnsweredEntry_LeavesTheQueue_AndOneToTryAgain_GoesWithTheNextSend()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_1", 1000, 0);
        queue.Record(Scope7, "ep_2", 2000, 0);
        var batch = queue.Claim(Scope7, 10);

        queue.Resolve([batch[0]], OfflineProgressOutcome.Done);
        queue.Resolve([batch[1]], OfflineProgressOutcome.Later);

        Assert.Equal([("ep_2", OfflineProgressState.Pending)], queue.Entries(Scope7).Select(e => (e.MediaItemId, e.State)));
        Assert.Equal(["ep_2"], queue.Claim(Scope7, 10).Select(e => e.MediaItemId));
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
    public void WhatTheServerAlreadyHas_LeavesTheQueue_AndWhatItDoesNotHave_Stays()
    {
        var queue = Open();
        var at = ManualTimeProvider.DefaultStart;
        queue.Record(Scope7, "ep_same", 1000, 0);
        queue.Record(Scope7, "ep_newer_there", 1000, 0);
        queue.Record(Scope7, "ep_older_there", 1000, 0);
        queue.Record(Scope7, "ep_not_there", 1000, 0);
        queue.Record(Scope8, "ep_same", 1000, 0);

        int gone = queue.DropKnown(Scope7, new Dictionary<string, KnownProgress>
        {
            ["ep_same"] = new(at, 1000),
            ["ep_newer_there"] = new(at.AddMinutes(5), 99_000),
            ["ep_older_there"] = new(at.AddMilliseconds(-1), 1000),
        });

        // The server's row at or after the entry's time says it has the position, or a newer one: a send would change nothing.
        Assert.Equal(2, gone);
        Assert.Equal(["ep_older_there", "ep_not_there"], queue.Entries(Scope7).Select(e => e.MediaItemId));
        Assert.All(queue.Entries(Scope7), e => Assert.Equal(OfflineProgressState.Pending, e.State));
        Assert.Single(queue.Entries(Scope8));
    }

    [Fact]
    public void AServerThatKeepsWholeSeconds_IsKnownToHaveAnEntry_OnlyByItsSecondAndItsVeryPosition()
    {
        var queue = Open();
        _time.Advance(TimeSpan.FromMilliseconds(800));
        var second = ManualTimeProvider.DefaultStart;
        queue.Record(Scope7, "ep_mine", 61_500, 0);
        queue.Record(Scope7, "ep_older_in_the_second", 61_500, 0);
        queue.Record(Scope7, "ep_exact_clock", 61_500, 0);
        queue.Record(Scope7, "ep_second_before", 61_500, 0);

        int gone = queue.DropKnown(Scope7, new Dictionary<string, KnownProgress>
        {
            // The entry's own row, as a server on SQLite lists it: the time cut to the second, the same position.
            ["ep_mine"] = new(second, 61_500),
            // Another position of that second: it may be an older one, so the entry is still sent.
            ["ep_older_in_the_second"] = new(second, 60_000),
            // A server that keeps fractions is compared exactly: 300 ms is before 800 ms.
            ["ep_exact_clock"] = new(second.AddMilliseconds(300), 61_500),
            ["ep_second_before"] = new(second.AddSeconds(-1), 61_500),
        });

        Assert.Equal(1, gone);
        Assert.Equal(["ep_older_in_the_second", "ep_exact_clock", "ep_second_before"], queue.Entries(Scope7).Select(e => e.MediaItemId));
    }

    [Fact]
    public void AnEntryOnItsWay_IsNotTakenByWhatTheServerSaid()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 1000, 0);
        var batch = queue.Claim(Scope7, 10);

        queue.DropKnown(Scope7, new Dictionary<string, KnownProgress> { ["ep_5"] = new(ManualTimeProvider.DefaultStart, 1000) });

        // Its own answer says what becomes of it.
        Assert.Equal(batch, queue.Entries(Scope7));
    }

    [Fact]
    public async Task AnEntryThatWasOnItsWayWhenGranaryClosed_IsSentAgainAfterTheNextStart()
    {
        var queue = Open();
        queue.Record(Scope7, "ep_5", 10_000, 0);
        queue.Claim(Scope7, 10);
        await queue.LatestSave;

        var entry = Assert.Single(Open().Entries(Scope7));

        // Nothing says the server has it, and a second send of the same position changes nothing there.
        Assert.Equal(OfflineProgressState.Pending, entry.State);
    }

    [Fact]
    public void AFileOfAnEarlierBuild_WithHeldEntries_IsRead_AndTheyAreSentAgain()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """
            { "version": 1, "entries": [
              { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "ep_5", "position_ms": 5000,
                "duration_ms": 0, "updated_at": "2026-10-01T11:00:00+00:00", "state": "Held" },
              { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "ep_6", "position_ms": 6000,
                "duration_ms": 0, "updated_at": "2026-10-01T11:30:00+00:00", "state": "Sending" } ] }
            """);

        var queue = Open();

        Assert.Equal([("ep_5", OfflineProgressState.Pending), ("ep_6", OfflineProgressState.Pending)],
            queue.Entries(Scope7).Select(e => (e.MediaItemId, e.State)));
    }

    [Theory]
    [InlineData("""{ "entries": [ { "media_item_id": "ep_5" }, null, { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "", "position_ms": 5 } ] }""")]
    [InlineData("""{ "entries": [ { "scope": { "server_entry_id": "srv1", "account_id": "1", "profile_id": "7" }, "media_item_id": "ep_5", "position_ms": -4, "duration_ms": 0, "updated_at": "2026-10-01T12:00:00+00:00" } ] }""")]
    public void EntriesThatSayNothingUsable_AreLeftOut_AndTheNextRecordIsKept(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);
        var queue = Open();

        Assert.Empty(queue.Entries(Scope7));
        queue.Record(Scope7, "ep_5", 10_000, 0);

        Assert.Single(queue.Entries(Scope7));
    }

    [Fact]
    public async Task AFileThatIsNotJson_IsMovedAside_NotWrittenOver()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ not json");
        var queue = Open();

        Assert.Empty(queue.Entries(Scope7));
        queue.Record(Scope7, "ep_5", 10_000, 0);
        await queue.LatestSave;

        // What could not be read is still there to look at; the queue starts over beside it.
        Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath + ".bad", Token));
        Assert.Contains("\"media_item_id\": \"ep_5\"", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.Contains("moved aside", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatCannotBeReadNow_IsNeverWrittenOver_AndIsReadOnceItCanBe()
    {
        var before = Open();
        before.Record(Scope7, "ep_old", 5_000, 0);
        before.Record(Scope7, "ep_5", 5_000, 0);
        await before.LatestSave;
        string kept = await File.ReadAllTextAsync(FilePath, Token);
        _time.Advance(TimeSpan.FromMinutes(1));
        var queue = Open();

        // Something has the file open and shares nothing, as a virus scanner at the start.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(queue.Entries(Scope7));
            queue.Record(Scope7, "ep_5", 10_000, 0);
            await queue.LatestSave;

            // The position is kept for this run, and the file is as it was: an empty queue was not written over it.
            Assert.Equal(10_000, Assert.Single(queue.Entries(Scope7)).PositionMs);
        }
        Assert.Equal(kept, await File.ReadAllTextAsync(FilePath, Token));
        Assert.Contains("could not be read", _logs.All, StringComparison.Ordinal);

        // The next write reads the file first: what it held is back, and the newer position of a title holds.
        queue.Record(Scope7, "ep_6", 20_000, 0);
        await queue.LatestSave;

        Assert.Equal([("ep_old", 5_000L), ("ep_5", 10_000L), ("ep_6", 20_000L)],
            queue.Entries(Scope7).OrderBy(e => e.PositionMs).Select(e => (e.MediaItemId, e.PositionMs)));
        Assert.Equal(3, Open().Entries(Scope7).Count);
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
        // A folder where the file should be: neither read nor written.
        Directory.CreateDirectory(FilePath);
        var queue = Open();

        queue.Record(Scope7, "ep_5", 10_000, 0);
        await queue.LatestSave;

        Assert.Single(queue.Entries(Scope7));
        Assert.Contains("could not be", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_TakesEveryEntryOfTheScopesItIsAskedFor_AlsoFromTheFile()
    {
        var other = new DownloadScope("srv2", "1", "7");
        var queue = Open();
        queue.Record(Scope7, "ep_1", 1000, 0);
        queue.Record(Scope8, "ep_2", 2000, 0);
        queue.Record(other, "ep_3", 3000, 0);
        queue.Claim(Scope8, 10);

        queue.Remove(scope => scope.ServerEntryId == "srv1");
        await queue.LatestSave;

        Assert.Empty(queue.Entries(Scope7));
        Assert.Empty(queue.Entries(Scope8));
        Assert.Single(queue.Entries(other));
        string file = await File.ReadAllTextAsync(FilePath, Token);
        Assert.DoesNotContain("ep_1", file, StringComparison.Ordinal);
        Assert.DoesNotContain("ep_2", file, StringComparison.Ordinal);
        Assert.Contains("ep_3", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatWasRemovedWhileTheFileCouldNotBeRead_DoesNotComeBackWithIt()
    {
        var before = Open();
        before.Record(Scope7, "ep_1", 1000, 0);
        before.Record(Scope8, "ep_2", 2000, 0);
        await before.LatestSave;
        var queue = Open();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            queue.Remove(scope => scope == Scope7);
            await queue.LatestSave;
        }
        queue.Record(Scope8, "ep_3", 3000, 0);
        await queue.LatestSave;

        Assert.Empty(queue.Entries(Scope7));
        Assert.Equal(2, queue.Entries(Scope8).Count);
        Assert.DoesNotContain("ep_1", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheFile_AndTakesAWritesLeftoverAway_ButNotOneBeingWritten()
    {
        var before = Open();
        before.Record(Scope7, "ep_5", 10_000, 0);
        await before.LatestSave;
        // A write that was cut off left its temporary file behind; another is a second write's, still on its way.
        string leftover = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string fresh = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(leftover, "{");
        File.WriteAllText(fresh, "{");
        File.SetLastWriteTimeUtc(leftover, _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(fresh, _time.GetUtcNow().UtcDateTime);
        var queue = Open();

        await queue.LoadAsync();

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(fresh));
        Assert.Single(queue.Entries(Scope7));
    }

    [Fact]
    public async Task ATitlesTimes_NeverGoBackwards_WhenThePCsClockIsSetBack()
    {
        var early = Open();
        _time.Advance(TimeSpan.FromHours(1));
        var first = early.Record(Scope7, "ep_5", 10_000, 0);
        await early.LatestSave;

        // Windows corrects the clock by half an hour (a clock of its own here: the test's only moves forward); the viewer
        // watches on.
        var corrected = new ManualTimeProvider();
        corrected.Advance(TimeSpan.FromMinutes(30));
        var queue = new OfflineProgressQueue(FilePath, corrected, _logs.For<OfflineProgressQueue>());
        _opened.Add(queue);
        var second = queue.Record(Scope7, "ep_5", 20_000, 0);
        var third = queue.Record(Scope7, "ep_5", 30_000, 0);

        // Each is later than the one it replaces, so the server keeps the newest: a millisecond is enough.
        Assert.Equal(first.UpdatedAt.AddMilliseconds(1), second.UpdatedAt);
        Assert.Equal(first.UpdatedAt.AddMilliseconds(2), third.UpdatedAt);
        // Another title is stamped by the clock as it is.
        Assert.Equal(corrected.GetUtcNow(), queue.Record(Scope7, "ep_6", 10_000, 0).UpdatedAt);
    }

    [Fact]
    public void APosition_IsStampedAfterWhatThisPCAlreadyKnewOfTheTitle()
    {
        var queue = Open();
        // The server's progress, read before, names 13:00 for the title; this PC's clock is an hour behind.
        var known = ManualTimeProvider.DefaultStart.AddHours(1);

        var entry = queue.Record(Scope7, "ep_5", 10_000, 0, after: known);

        Assert.Equal(known.AddMilliseconds(1), entry.UpdatedAt);
        // A clock that is ahead of what is known stamps as it reads.
        Assert.Equal(_time.GetUtcNow(), queue.Record(Scope7, "ep_6", 10_000, 0, after: ManualTimeProvider.DefaultStart.AddHours(-1)).UpdatedAt);
    }
}
