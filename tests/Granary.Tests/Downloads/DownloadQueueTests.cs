using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The queue: two transfers at once for the active profile, what stops them (a metered connection, a profile switch, a
/// sign-in that expired, a pause, a delete) and how they carry on, the retry schedule, and the status reports.
/// </summary>
public sealed class DownloadQueueTests : IAsyncDisposable
{
    private static readonly byte[] Source = DownloadPayloads.Source(3000);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private Task CompletedAsync(string id) =>
        Until(() => _h.Find(id)?.Record.State == DownloadState.Completed, id + " completed");

    private static Task IdleAsync(DownloadQueue queue) => queue.Idle.WaitAsync(Settle, Token);

    [Fact]
    public async Task TwoRunAtOnce_TheThirdWaits()
    {
        await _h.AddAsync("dl_1");
        await _h.AddAsync("dl_2");
        await _h.AddAsync("dl_3");
        var first = _h.ServeGated("dl_1", Source);
        _h.ServeGated("dl_2", Source);
        _h.ServeGated("dl_3", Source).ReleaseAll();

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.Validator is not null && _h.Find("dl_2")!.Record.Validator is not null, "two transfers ran");
        await queue.Parked.WaitAsync(Settle, Token);

        Assert.Empty(_h.FileRequests("dl_3"));
        first.ReleaseAll();
        await CompletedAsync("dl_3");
        Assert.Equal(DownloadState.Completed, _h.Find("dl_1")!.Record.State);
        Assert.Equal(DownloadState.Downloading, _h.Find("dl_2")!.Record.State);
    }

    [Fact]
    public async Task ConnectionTurningMetered_PausesTheTransfer_AndKeepsItsBytes()
    {
        await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source, "\"v1\"",
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[2048..], "\"v1\"", 2048, 3000));
        var queue = _h.StartQueue();
        body.Release(2048);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 2048, "2 KB arrived");

        _h.Network.SetMetered(true);
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Queued, "the transfer stopped");
        await IdleAsync(queue);

        Assert.True(queue.IsWaitingForUnmetered);
        var download = _h.Find("dl_1")!;
        Assert.True(new FileInfo(download.PartPath).Length >= 2048);
        Assert.Equal("\"v1\"", download.Record.Validator);
        Assert.Single(_h.FileRequests("dl_1"));

        _h.Network.SetMetered(false);
        await CompletedAsync("dl_1");
        var resumed = _h.FileRequests("dl_1")[^1];
        Assert.Equal(("bytes=2048-", "\"v1\""), (resumed.Range, resumed.IfRange));
        Assert.Equal(Source, await File.ReadAllBytesAsync(_h.Find("dl_1")!.MediaPath!, Token));
        Assert.False(queue.IsWaitingForUnmetered);
    }

    [Fact]
    public async Task OnAMeteredConnection_NothingStarts_UnlessTheSettingAllowsIt()
    {
        _h.Network.Current = _h.Network.Current with { IsMetered = true };
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);

        var queue = _h.StartQueue();
        await IdleAsync(queue);

        Assert.Empty(_h.FileRequests("dl_1"));
        Assert.True(queue.IsWaitingForUnmetered);
        await _h.Preferences.SetDownloadsOnMeteredAsync(true);
        await CompletedAsync("dl_1");
        Assert.False(queue.IsWaitingForUnmetered);
    }

    [Fact]
    public async Task GranaryClosedMidWrite_ResumesFromThePartAtTheNextStart()
    {
        const int SixMiB = 6 * 1024 * 1024;
        byte[] source = DownloadPayloads.Source(8 * 1024 * 1024);
        var stored = await _h.AddAsync("dl_1", DownloadState.Downloading, source.Length, validator: "\"v1\"");
        await File.WriteAllBytesAsync(stored.PartPath, source[..SixMiB], Token);
        await _h.RestartAsync();
        long from = SixMiB - DownloadQueue.ResumeMargin;
        _h.ServeFile("dl_1", DownloadPayloads.Bytes(HttpStatusCode.PartialContent, source[(int)from..], "\"v1\"", from, source.Length));

        _h.StartQueue();
        await CompletedAsync("dl_1");

        var sent = Assert.Single(_h.FileRequests("dl_1"));
        Assert.Equal(("bytes=2097152-", "\"v1\""), (sent.Range, sent.IfRange));
        Assert.Equal(source, await File.ReadAllBytesAsync(_h.Find("dl_1")!.MediaPath!, Token));
        Assert.False(File.Exists(_h.Find("dl_1")!.PartPath));
    }

    [Fact]
    public async Task Closing_LeavesTheTransferDownloading_SoTheNextStartTakesTheMargin()
    {
        await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source);
        var queue = _h.StartQueue();
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        await queue.StopAsync();

        var download = _h.Find("dl_1")!;
        Assert.Equal((DownloadState.Downloading, "\"v1\""), (download.Record.State, download.Record.Validator));
        Assert.Equal(1024, new FileInfo(download.PartPath).Length);
    }

    [Fact]
    public async Task AProfileSwitch_PausesTheOldProfilesTransfer_AndItCarriesOnWhenItIsBack()
    {
        await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source, "\"v1\"",
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1024..], "\"v1\"", 1024, 3000));
        var queue = _h.StartQueue();
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        _h.SwitchProfile("8");
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Queued, "the transfer stopped");
        await IdleAsync(queue);

        Assert.Equal(1024, new FileInfo(_h.Find("dl_1")!.PartPath).Length);
        Assert.Single(_h.FileRequests("dl_1"));
        Assert.DoesNotContain(_h.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/downloads/dl_1", StringComparison.Ordinal) && r.AsProfile != "7");

        _h.SwitchProfile("7");
        await CompletedAsync("dl_1");
        var resumed = _h.FileRequests("dl_1")[^1];
        Assert.Equal(("bytes=1024-", "\"v1\"", "7"), (resumed.Range, resumed.IfRange, resumed.AsProfile));
    }

    [Fact]
    public async Task AnExpiredSignIn_StopsTheQueue_AndKeepsTheFiles()
    {
        var stored = await _h.AddAsync("dl_1", validator: "\"v1\"");
        await File.WriteAllBytesAsync(stored.PartPath, Source[..1000], Token);
        _h.ServeFile("dl_1", ScriptedSilo.Problem(401, "session_expired"));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the transfer asked");
        await IdleAsync(queue);

        _h.SignOut();
        queue.Wake();
        await _h.AdvanceAsync(TimeSpan.FromMinutes(10), queue);

        Assert.Single(_h.FileRequests("dl_1"));
        var download = _h.Find("dl_1")!;
        Assert.Equal((DownloadState.Queued, DownloadFailure.None, "\"v1\""), (download.Record.State, download.Record.Failure, download.Record.Validator));
        Assert.Equal(Source[..1000], await File.ReadAllBytesAsync(download.PartPath, Token));
    }

    [Fact]
    public async Task ARefusedSignIn_StopsTheProfilesDownloads_UntilTheSessionChanges()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(401, "invalid_token"), DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the transfer asked");
        await IdleAsync(queue);

        queue.Wake();
        await _h.AdvanceAsync(TimeSpan.FromMinutes(10), queue);
        Assert.Single(_h.FileRequests("dl_1"));

        _h.Session.RaiseChanged();
        await CompletedAsync("dl_1");
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
    }

    [Fact]
    public async Task RateLimited_WaitsForRetryAfter()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(429, "rate_limited", TimeSpan.FromSeconds(40)), DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the transfer asked");
        await IdleAsync(queue);

        await _h.AdvanceAsync(TimeSpan.FromSeconds(39), queue);
        Assert.Single(_h.FileRequests("dl_1"));

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
    }

    [Fact]
    public async Task AServerAskingForHours_IsAskedAgainAfterFiveMinutes()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable", TimeSpan.FromHours(3)),
            DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the transfer asked");
        await IdleAsync(queue);

        await _h.AdvanceAsync(TimeSpan.FromSeconds(299), queue);
        Assert.Single(_h.FileRequests("dl_1"));
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");
    }

    [Fact]
    public async Task ALostConnection_TriesAgainLater()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the first try");
        await IdleAsync(queue);

        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.FileRequests("dl_1").Count == 2, "the try after 1 s");
        await IdleAsync(queue);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        Assert.Equal(2, _h.FileRequests("dl_1").Count);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");
        Assert.Equal(3, _h.FileRequests("dl_1").Count);
    }

    [Fact]
    public async Task TheTransfer_ReportsDownloadingThenCompleted()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);

        _h.StartQueue();
        await CompletedAsync("dl_1");
        await Until(() => _h.Reports("dl_1").Count == 2 && _h.Find("dl_1")!.Record.PendingReport is null, "both reports went");

        Assert.Equal(
        [
            """{"status":"downloading","updated_at":"2026-10-01T12:00:00+00:00","revision":1}""",
            """{"status":"completed","updated_at":"2026-10-01T12:00:00+00:00","revision":1}""",
        ], _h.Reports("dl_1").Select(r => r.Body));
        Assert.All(_h.Reports("dl_1"), r => Assert.Equal("7", r.AsProfile));
    }

    [Fact]
    public async Task AReportThatFailed_IsSentAgainWithTheSameTime()
    {
        await _h.AddAsync("dl_1");
        _h.ServeGated("dl_1", Source);
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")));
        var queue = _h.StartQueue();
        // A pass the transfer's own writes start may send it again before the test looks.
        await Until(() => _h.Reports("dl_1").Count >= 1, "the first report");

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        queue.Wake();
        await Until(() => _h.Reports("dl_1").Count == 2 && _h.Find("dl_1")!.Record.PendingReport is null, "the report went again");

        var reports = _h.Reports("dl_1");
        Assert.Equal(reports[0].Body, reports[1].Body);
        Assert.Contains("\"updated_at\":\"2026-10-01T12:00:00+00:00\"", reports[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriveThatIsGone_WaitsAsMissingFiles_AndCarriesOnWhenItIsBack()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        string away = stored.Folder + "-away";
        Directory.Move(stored.Folder, away);
        _h.Disk.Set(null);

        var queue = _h.StartQueue();
        await IdleAsync(queue);
        Assert.True(_h.Find("dl_1")!.FilesMissing);
        Assert.Empty(_h.FileRequests("dl_1"));

        Directory.Move(away, stored.Folder);
        _h.Disk.Set(1L << 40);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await CompletedAsync("dl_1");
        Assert.False(_h.Find("dl_1")!.FilesMissing);
    }

    [Fact]
    public async Task AFolderWhoseDriveDoesNotAnswer_IsTriedAgainLater()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        _h.Disk.Set(null);

        var queue = _h.StartQueue();
        await IdleAsync(queue);
        Assert.Equal(DownloadState.Queued, _h.Find("dl_1")!.Record.State);
        Assert.Empty(_h.FileRequests("dl_1"));

        _h.Disk.Set(1L << 40);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");
    }

    [Fact]
    public async Task AFullDrive_FailsWithNotEnoughSpace_AndKeepsTheBytes()
    {
        var stored = await _h.AddAsync("dl_1", validator: "\"v1\"");
        await File.WriteAllBytesAsync(stored.PartPath, Source[..1000], Token);
        _h.ServeFile("dl_1", Source);
        _h.Disk.Set(DownloadTransfer.SpaceReserve + 100);

        var queue = _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Failed, "it failed");
        await _h.AdvanceAsync(TimeSpan.FromHours(1), queue);

        var download = _h.Find("dl_1")!;
        Assert.Equal((DownloadFailure.NotEnoughSpace, "\"v1\""), (download.Record.Failure, download.Record.Validator));
        Assert.Equal(1000, new FileInfo(download.PartPath).Length);
        Assert.Empty(_h.FileRequests("dl_1"));
    }

    [Fact]
    public async Task AFileOfAnotherLengthTwice_FailsItsIntegrity()
    {
        await _h.AddAsync("dl_1", size: 4000);
        _h.ServeFile("dl_1", Source);

        _h.StartQueue();
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Failed, "it failed");

        Assert.Equal(DownloadFailure.Integrity, _h.Find("dl_1")!.Record.Failure);
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
        Assert.All(_h.FileRequests("dl_1"), r => Assert.Null(r.Range));
    }

    [Fact]
    public async Task APause_StopsTheTransfer_AndNothingStartsUntilResumed()
    {
        await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source, "\"v1\"",
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1024..], "\"v1\"", 1024, 3000));
        var queue = _h.StartQueue();
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsPausedByViewer = true }, Token);
        await Until(() => _h.Find("dl_1")!.Record.State == DownloadState.Queued, "the transfer stopped");
        await IdleAsync(queue);
        queue.Wake();
        await _h.AdvanceAsync(TimeSpan.FromMinutes(10), queue);
        Assert.Single(_h.FileRequests("dl_1"));

        await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { IsPausedByViewer = false }, Token);
        await CompletedAsync("dl_1");
        Assert.Equal("bytes=1024-", _h.FileRequests("dl_1")[^1].Range);
    }

    [Fact]
    public async Task ADeletedDownload_StopsItsTransfer_AndItsFolderGoes()
    {
        var stored = await _h.AddAsync("dl_1");
        var body = _h.ServeGated("dl_1", Source);
        var queue = _h.StartQueue();
        body.Release(1024);
        await Until(() => queue.BytesDone(Scope7, "dl_1") == 1024, "1 KB arrived");

        await _h.Store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        Assert.False(Directory.Exists(stored.Folder));
        await IdleAsync(queue);
        Assert.Single(_h.FileRequests("dl_1"));
        Assert.Null(_h.Find("dl_1"));
    }

    [Fact]
    public async Task AnotherProfilesDownloads_WaitForThatProfile()
    {
        await _h.AddAsync("dl_8", scope: Scope8);
        _h.ServeFile("dl_8", Source);

        var queue = _h.StartQueue();
        await IdleAsync(queue);
        Assert.Empty(_h.FileRequests("dl_8"));

        _h.SwitchProfile("8");
        await Until(() => _h.Find("dl_8", Scope8)?.Record.State == DownloadState.Completed, "profile 8's download completed");
        Assert.Equal("8", _h.FileRequests("dl_8")[0].AsProfile);
    }
}
