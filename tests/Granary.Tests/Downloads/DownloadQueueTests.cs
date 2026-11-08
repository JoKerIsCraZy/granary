using System.Net;
using System.Text.Json;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;
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

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private Task CompletedAsync(string id) =>
        Until(() => _h.Find(id)?.Record.State == DownloadState.Completed, id + " completed");

    // A fresh look of the queue, so what it did not start is not taken from a look before the event.
    private static Task IdleAsync(DownloadQueue queue) => SettleAsync(queue);

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
        await SettleRunningAsync(queue);

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
        await Until(() => _h.Logs.All.Contains("could not be sent", StringComparison.Ordinal), "the first report failed");

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

        await Until(() => !Directory.Exists(stored.Folder), "the folder went");
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

    [Theory]
    [InlineData(503, null)]
    [InlineData(429, 0)]
    public async Task ABusyServerThatNamesNoWait_IsAskedLessAndLessOften(int status, int? retryAfter)
    {
        await _h.AddAsync("dl_1");
        var busy = ScriptedSilo.Problem(status, status == 429 ? "rate_limited" : "dependency_unavailable",
            retryAfter is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        _h.ServeFile("dl_1", busy, busy, busy, DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));
        var queue = _h.StartQueue();
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the first try");
        await IdleAsync(queue);

        // 1 s, then 2 s, then 4 s: never at once, never at a fixed short pace.
        Assert.Single(_h.FileRequests("dl_1"));
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.FileRequests("dl_1").Count == 2, "the second try");
        await IdleAsync(queue);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        await Until(() => _h.FileRequests("dl_1").Count == 3, "the third try");
        await IdleAsync(queue);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(3), queue);
        Assert.Equal(3, _h.FileRequests("dl_1").Count);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await CompletedAsync("dl_1");
    }

    [Fact]
    public async Task ARefusedSignIn_AfterTheSessionChangedMeanwhile_DoesNotStopTheProfile()
    {
        await _h.AddAsync("dl_1");
        // The session changes (a sign-in that came back) while the refused request is under way.
        _h.ServeFile("dl_1", () =>
        {
            _h.Session.RaiseChanged();
            return ScriptedSilo.Problem(401, "invalid_token")();
        }, DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));

        _h.StartQueue();

        await CompletedAsync("dl_1");
        Assert.Equal(2, _h.FileRequests("dl_1").Count);
    }

    [Fact]
    public async Task AReportInItsBackoff_IsNotSentAgainAtEveryWake()
    {
        await _h.AddAsync("dl_1");
        _h.ServeGated("dl_1", Source);
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")));
        var queue = _h.StartQueue();
        await Until(() => _h.Logs.All.Contains("could not be sent", StringComparison.Ordinal), "the first report failed");

        await SettleRunningAsync(queue);
        await SettleRunningAsync(queue);
        Assert.Single(_h.Reports("dl_1"));

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _h.Find("dl_1")!.Record.PendingReport is null, "the report went after its delay");
        Assert.Equal(2, _h.Reports("dl_1").Count);
    }

    [Fact]
    public async Task ARefusedSignInOnAReport_KeepsIt_ForLater()
    {
        await _h.AddAsync("dl_1");
        _h.ServeGated("dl_1", Source);
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(401, "session_expired"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")));
        var queue = _h.StartQueue();
        await Until(() => _h.Logs.All.Contains("could not be sent", StringComparison.Ordinal), "the first report failed");
        await SettleRunningAsync(queue);

        Assert.NotNull(_h.Find("dl_1")!.Record.PendingReport);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _h.Find("dl_1")!.Record.PendingReport is null, "the report went again");
        Assert.Equal(_h.Reports("dl_1")[0].Body, _h.Reports("dl_1")[1].Body);
    }

    [Fact]
    public async Task AStatusReportThatHangs_HoldsBackNoOtherTransfer()
    {
        await _h.AddAsync("dl_1");
        await _h.AddAsync("dl_2");
        await _h.AddAsync("dl_3");
        var hanging = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _h.ServeFile("dl_1", Source);
            _h.AcceptReports("dl_1", ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")), () => hanging.Task);
            _h.ServeGated("dl_2", Source);
            _h.ServeFile("dl_3", Source);

            _h.StartQueue();

            await CompletedAsync("dl_1");
            await CompletedAsync("dl_3");
            Assert.NotNull(_h.Find("dl_1")!.Record.PendingReport);
        }
        finally
        {
            hanging.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DownloadPayloads.Entry("dl_1", "completed"), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task FoldersAreLookedAtAgainOnceAMinute_NotAtEveryWake()
    {
        var stored = await _h.AddAsync("dl_1", DownloadState.Completed);
        var queue = _h.StartQueue();
        await IdleAsync(queue);

        Directory.Move(stored.Folder, stored.Folder + "-away");
        await IdleAsync(queue);
        Assert.False(_h.Find("dl_1")!.FilesMissing);

        await _h.AdvanceAsync(TimeSpan.FromMinutes(1), queue);
        Assert.True(_h.Find("dl_1")!.FilesMissing);
    }

    [Fact]
    public async Task ALookThatFails_DoesNotEndTheQueue()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        _h.Network.Fails = new InvalidOperationException("The system did not answer.");
        var queue = _h.StartQueue();
        await Until(() => _h.Logs.All.Contains("failed", StringComparison.Ordinal), "the failure was logged");
        await Until(() => queue.Passes >= 1, "a pass ended");

        _h.Network.Fails = null;
        queue.Wake();
        await CompletedAsync("dl_1");
    }

    [Fact]
    public async Task Stop_WaitsAtMostItsBudget_ForATransferThatDoesNotLetGo()
    {
        await _h.AddAsync("dl_1");
        var stubborn = new StubbornStream();
        _h.ServeFile("dl_1", DownloadPayloads.Streamed(HttpStatusCode.OK, stubborn, 3000, "\"v1\""));
        var queue = _h.StartQueue();
        await stubborn.Reading.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var stop = queue.StopAsync();
        await _h.Time.WaitUntilAsync(() => _h.Time.NextDue is { } due && due <= DownloadQueue.StopBudget, Token);
        Assert.False(stop.IsCompleted);
        _h.Time.Advance(DownloadQueue.StopBudget);
        await stop.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Contains("did not stop within", _h.Logs.All, StringComparison.Ordinal);
        stubborn.Release();
    }

    // A transfer that resumes 1000 bytes in, its manifest read and its validator kept, and waits for the rest: nothing it does
    // writes the record after "downloading", so only the queue's own timers wake the loop.
    private async Task<GatedMediaStream> ResumingAsync()
    {
        var stored = await _h.AddAsync("dl_1", validator: "\"v1\"", change: r => r with { ManifestReadAt = ManualTimeProvider.DefaultStart });
        await File.WriteAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), DownloadPayloads.Manifest("dl_1"), Token);
        await File.WriteAllBytesAsync(stored.PartPath, Source[..1000], Token);
        var rest = new GatedMediaStream(Source[1000..]);
        _h.ServeFile("dl_1", DownloadPayloads.Streamed(HttpStatusCode.PartialContent, rest, 2000, "\"v1\"", 1000, 3000));
        return rest;
    }

    private string[] ReportStatuses() =>
        [.. _h.Reports("dl_1").Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("status").GetString()!)];

    [Fact]
    public async Task AFailedDownloadingReport_IsSentAgainWhileTheTransferRuns()
    {
        await ResumingAsync();
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")));
        _h.StartQueue();
        await Until(() => _h.Logs.All.Contains("could not be sent", StringComparison.Ordinal), "the first report failed");

        _h.Time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _h.Find("dl_1")!.Record.PendingReport is null, "the report went again");
        Assert.Equal(["downloading", "downloading"], ReportStatuses());
        Assert.Equal(DownloadState.Downloading, _h.Find("dl_1")!.Record.State);
    }

    [Fact]
    public async Task ADownloadingReportThatTakesTooLong_IsLeftForLater_AndTheFileIsAsked()
    {
        await ResumingAsync();
        int reports = 0;
        _h.Silo.OnCancellable(HttpMethod.Patch, EntryPath("dl_1"), async cancellationToken =>
        {
            if (Interlocked.Increment(ref reports) == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DownloadPayloads.Entry("dl_1", "downloading"), System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var queue = _h.StartQueue();
        await Until(() => _h.Reports("dl_1").Count == 1, "the report went");
        await SettleRunningAsync(queue);
        Assert.Empty(_h.FileRequests("dl_1"));

        _h.Time.Advance(DownloadQueue.ReportBudget);
        await Until(() => _h.FileRequests("dl_1").Count == 1, "the file was asked for");
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _h.Find("dl_1")!.Record.PendingReport is null, "the report went again");
        Assert.Equal(2, _h.Reports("dl_1").Count);
    }

    [Fact]
    public async Task AnUnsentDownloadingReport_StillGoesBeforeCompleted()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        _h.AcceptReports("dl_1", ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")));
        _h.StartQueue();
        await CompletedAsync("dl_1");
        Assert.Equal(["downloading"], ReportStatuses());

        _h.Time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _h.Find("dl_1")!.Record is { PendingReport: null, NextReport: null }, "both reports went");
        Assert.Equal(["downloading", "downloading", "completed"], ReportStatuses());
    }

    [Fact]
    public async Task TheFile_IsAskedForOnlyOnceDownloadingWasAnswered()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.AcceptReports("dl_1", () => answer.Task, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "completed")));
        var queue = _h.StartQueue();
        await Until(() => _h.Reports("dl_1").Count == 1, "downloading went");
        await SettleRunningAsync(queue);

        Assert.Empty(_h.FileRequests("dl_1"));
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(DownloadPayloads.Entry("dl_1", "downloading"), System.Text.Encoding.UTF8, "application/json"),
        });
        await CompletedAsync("dl_1");
        Assert.Single(_h.FileRequests("dl_1"));
    }

    [Fact]
    public async Task AReportThatFailsUnexpectedly_WaitsItsDelay_InsteadOfGoingAgainAtOnce()
    {
        await _h.AddAsync("dl_1");
        _h.ServeFile("dl_1", Source);
        _h.AcceptReports("dl_1", ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Entry("dl_1", "downloading")),
            () => throw new InvalidOperationException("The test's server broke."));
        var queue = _h.StartQueue();
        await CompletedAsync("dl_1");
        await Until(() => _h.Reports("dl_1").Count >= 2, "completed was tried");

        await SettleAsync(queue);
        Assert.Equal(2, _h.Reports("dl_1").Count);

        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        Assert.Equal(3, _h.Reports("dl_1").Count);
        await _h.AdvanceAsync(TimeSpan.FromSeconds(1), queue);
        Assert.Equal(3, _h.Reports("dl_1").Count);
    }

    [Fact]
    public async Task Stopping_WhileTheRegistryIsAsked_DoesNotThrow()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.OnCancellable(HttpMethod.Get, ListPath, async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count == 1, "the registry was asked");

        await queue.StopAsync();

        Assert.DoesNotContain(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposing_WhileARegistryReadHoldsItsTurn_LogsNoFailure()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        // An answer that comes whatever the cancellation says, after the queue is disposed.
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, ListPath, () => answer.Task);
        var queue = _h.StartQueue();
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, ListPath).Count == 1, "the registry was asked");

        queue.Dispose();
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "completed")), System.Text.Encoding.UTF8, "application/json"),
        });
        await Until(() => queue.Passes >= 1, "the pass ended");

        Assert.DoesNotContain(_h.Logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
        await queue.StopAsync();
    }

    [Fact]
    public async Task Dispose_StopsFollowing_AndCanBeCalledTwice()
    {
        await _h.AddAsync("dl_8", scope: Scope8);
        _h.ServeFile("dl_8", Source);
        var queue = _h.StartQueue();
        await IdleAsync(queue);
        long passes = queue.Passes;

        queue.Dispose();
        queue.Dispose();
        _h.SwitchProfile("8");
        _h.Network.SetMetered(false);
        await queue.StopAsync();

        Assert.Equal(passes, queue.Passes);
        Assert.Empty(_h.FileRequests("dl_8"));
    }
}

/// <summary>A media body whose read waits until the test lets it end, whatever the cancellation says.</summary>
internal sealed class StubbornStream : Stream
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a read waits.</summary>
    public Task Reading => _reading.Task;

    public void Release() => _release.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _reading.TrySetResult();
        await _release.Task;
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
