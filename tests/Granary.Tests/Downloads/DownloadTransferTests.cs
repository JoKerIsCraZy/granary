using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>One media file over HTTP into media.part: whole, resumed with Range and If-Range, and every way it can stop.</summary>
public sealed class DownloadTransferTests : IDisposable
{
    private const string FilePath = "api/v2/downloads/dl_1/file";
    private const string ModifiedText = "Thu, 01 Oct 2026 12:00:00 GMT";
    private static readonly DownloadScope Scope = new("srv1", "1", "7");
    private static readonly byte[] Source = DownloadPayloads.Source(3000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<long> _progress = [];
    private readonly List<(string? Validator, long PartLength)> _told = [];
    private FakeDiskSpace _disk = new();

    public DownloadTransferTests()
    {
        Directory.CreateDirectory(_folder);
    }

    private string PartPath => Path.Combine(_folder, DownloadPaths.MediaPart);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private DownloadTransfer Transfer(long spaceCheckBytes = 64L * 1024 * 1024, Action<long>? flushed = null) =>
        new(new DownloadsApi(_silo.Http), _disk, _time, _logs.For<DownloadTransfer>())
        {
            SpaceCheckBytes = spaceCheckBytes,
            Flushed = flushed,
        };

    private TransferJob Job(long expectedBytes = 3000, long resumeFrom = 0, string? validator = null, string id = "dl_1") =>
        new(ScriptedSilo.Base, Scope, id, _folder, expectedBytes, resumeFrom, validator);

    private Task<TransferOutcome> RunAsync(TransferJob job, DownloadTransfer? transfer = null, CancellationToken? cancellation = null) =>
        (transfer ?? Transfer()).RunAsync(job, _progress.Add, TellAsync, cancellation ?? Token);

    // As the queue keeps it: what the part's bytes are now, with the part as it is when told.
    private Task TellAsync(string? validator, CancellationToken cancellationToken)
    {
        _told.Add((validator, new FileInfo(PartPath).Length));
        return Task.CompletedTask;
    }

    private async Task<byte[]> PartAsync() => await File.ReadAllBytesAsync(PartPath, Token);

    [Fact]
    public async Task AFreshTransfer_WritesTheWholeFile()
    {
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));

        var outcome = await RunAsync(Job());

        Assert.Equal(new TransferOutcome(TransferResult.Completed, "\"v1\"", null, 3000), outcome);
        Assert.Equal(Source, await PartAsync());
        var sent = Assert.Single(_silo.Sent);
        Assert.Null(sent.Range);
        Assert.Equal("7", sent.AsProfile);
        Assert.Equal(3000, _progress[^1]);
        Assert.Equal([("\"v1\"", 0L)], _told);
    }

    [Fact]
    public async Task ACancelledTransfer_HasToldItsValidator_AndTheNextResumesWithIt()
    {
        var body = new TestMediaStream(Source) { ChunkSize = 500, StallAfter = 1000 };
        _silo.On(HttpMethod.Get, FilePath,
            DownloadPayloads.Streamed(HttpStatusCode.OK, body, 3000, "\"v1\""),
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v1\"", 1000, 3000));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var first = RunAsync(Job(), cancellation: stop.Token);
        await body.Stalling.WaitAsync(Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal([("\"v1\"", 0L)], _told);
        Assert.Equal(1000, new FileInfo(PartPath).Length);

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: _told[^1].Validator));

        Assert.Equal(TransferResult.Completed, outcome.Result);
        var resumed = _silo.Sent[^1];
        Assert.Equal(("bytes=1000-", "\"v1\""), (resumed.Range, resumed.IfRange));
        Assert.Equal(Source, await PartAsync());
        Assert.Single(_told);
    }

    [Fact]
    public async Task AResumedTransfer_AsksForTheRestWithIfRange()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v1\"", 1000, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Completed, "\"v1\"", null, 3000), outcome);
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal("bytes=1000-", sent.Range);
        Assert.Equal("\"v1\"", sent.IfRange);
        Assert.Equal(Source, await PartAsync());
        Assert.Empty(_told);
    }

    [Fact]
    public async Task ARangeAnsweredWith200_StartsTheFileOver()
    {
        await File.WriteAllBytesAsync(PartPath, DownloadPayloads.Source(1000, seed: 9), Token);
        byte[] fresh = DownloadPayloads.Source(3000, seed: 5);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.OK, fresh, "\"v2\""));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Completed, "\"v2\"", null, 3000), outcome);
        Assert.Equal(fresh, await PartAsync());
        // The old bytes were gone before the new validator was told: no crash can pair the two.
        Assert.Equal([("\"v2\"", 0L)], _told);
    }

    [Fact]
    public async Task A206OfAnotherVersion_StartsOver()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v2\"", 1000, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Integrity, null, null, 0), outcome);
        Assert.Equal(0, new FileInfo(PartPath).Length);
        Assert.Equal([(null, 0L)], _told);
    }

    [Fact]
    public async Task A206WithoutAValidator_KeepsTheOneOfTheBytes()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], null, 1000, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Completed, "\"v1\"", null, 3000), outcome);
        Assert.Empty(_told);
    }

    [Fact]
    public async Task A206ByDate_IsTheSameVersionOnlyAtTheSameDate()
    {
        var modified = DateTimeOffset.Parse(ModifiedText, System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath,
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], null, 1000, 3000, modified),
            DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], null, 1000, 3000, modified.AddHours(1)));

        Assert.Equal(TransferResult.Completed, (await RunAsync(Job(resumeFrom: 1000, validator: ModifiedText))).Result);

        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        Assert.Equal(TransferResult.Integrity, (await RunAsync(Job(resumeFrom: 1000, validator: ModifiedText))).Result);
        Assert.Equal(0, new FileInfo(PartPath).Length);
    }

    [Fact]
    public async Task APartLongerThanTheResume_IsCutBackFirst()
    {
        await File.WriteAllBytesAsync(PartPath, [.. Source[..1000], .. DownloadPayloads.Source(500, seed: 9)], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v1\"", 1000, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(TransferResult.Completed, outcome.Result);
        Assert.Equal(Source, await PartAsync());
    }

    [Fact]
    public async Task AResumePastTheFile_IsCutBackToIt()
    {
        await File.WriteAllBytesAsync(PartPath, Source, Token);
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.Problem(416, "range_not_satisfiable"));

        var outcome = await RunAsync(Job(resumeFrom: 5000, validator: "\"v1\""));

        Assert.Equal(TransferResult.Completed, outcome.Result);
        Assert.Equal("bytes=3000-", Assert.Single(_silo.Sent).Range);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("W/\"weak\"")]
    public async Task AResumeWithoutAValidatorIfRangeTakes_StartsOver(string? validator)
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: validator));

        Assert.Equal(TransferResult.Completed, outcome.Result);
        Assert.Null(Assert.Single(_silo.Sent).Range);
        Assert.Equal(Source, await PartAsync());
    }

    [Fact]
    public async Task AStartOver_NeedsRoomForTheWholeFile()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _disk = new FakeDiskSpace(DownloadTransfer.SpaceReserve + 2500);

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: null));

        Assert.Equal(TransferResult.NotEnoughSpace, outcome.Result);
        Assert.Empty(_silo.Sent);
        Assert.Equal(1000, new FileInfo(PartPath).Length);
    }

    [Fact]
    public async Task Past4GB_SizesAndContentRangeStayLong()
    {
        const long expected = 5_000_000_010;
        await File.WriteAllBytesAsync(PartPath, DownloadPayloads.Source(10), Token);
        _disk = new FakeDiskSpace(DownloadTransfer.SpaceReserve + 5_000_000_000);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, DownloadPayloads.Source(10, seed: 3), "\"v1\"", 10, expected));

        var outcome = await RunAsync(Job(expected, resumeFrom: 10, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Unreachable, "\"v1\"", null, 20), outcome);
        Assert.Equal(20, _progress[^1]);
        Assert.Equal(20, new FileInfo(PartPath).Length);
        Assert.Equal("bytes=10-", Assert.Single(_silo.Sent).Range);

        // One byte less than the 5 GB still to come, beside the reserve, is not enough.
        _disk.Set(DownloadTransfer.SpaceReserve + 4_999_999_999);
        var refused = await RunAsync(Job(expected, resumeFrom: 10, validator: "\"v1\""));

        Assert.Equal(TransferResult.NotEnoughSpace, refused.Result);
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task TheDriveFillingUp_StopsWithNotEnoughSpace_AndKeepsThePart()
    {
        byte[] source = DownloadPayloads.Source(8192);
        _disk = new FakeDiskSpace(10L << 30, 100L << 20);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Streamed(HttpStatusCode.OK, new TestMediaStream(source) { ChunkSize = 512 }, 8192, "\"v1\""));

        var outcome = await RunAsync(Job(8192), Transfer(spaceCheckBytes: 1024));

        Assert.Equal(TransferResult.NotEnoughSpace, outcome.Result);
        Assert.Equal("\"v1\"", outcome.Validator);
        long kept = new FileInfo(PartPath).Length;
        Assert.InRange(kept, 1024, 8191);
        Assert.Equal(kept, outcome.Bytes);
        Assert.Equal(source[..(int)kept], await PartAsync());
    }

    [Fact]
    public async Task NotEnoughSpaceBeforeTheStart_SendsNothing()
    {
        _disk = new FakeDiskSpace(1L << 30);

        var outcome = await RunAsync(Job(5L << 30));

        Assert.Equal(TransferResult.NotEnoughSpace, outcome.Result);
        Assert.Empty(_silo.Sent);
        Assert.Equal([_folder], _disk.Asked);
    }

    [Fact]
    public async Task ThePart_IsOnTheDiskEvery3MiB_AndAtTheEnd()
    {
        const int size = 10 << 20;
        byte[] source = DownloadPayloads.Source(size);
        var flushes = new List<long>();
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Streamed(HttpStatusCode.OK, new TestMediaStream(source) { ChunkSize = 1 << 20 }, size, "\"v1\""));

        var outcome = await RunAsync(Job(size), Transfer(flushed: flushes.Add));

        Assert.Equal(TransferResult.Completed, outcome.Result);
        Assert.Equal(size, flushes[^1]);
        // At most the queue's 4 MiB resume margin is ever written but not yet on the disk.
        Assert.All(flushes.Zip([0L, .. flushes]), pair => Assert.InRange(pair.First - pair.Second, 1, 4L << 20));
    }

    [Fact]
    public async Task ABodyThatBreaksOff_KeepsWhatArrived()
    {
        _silo.On(HttpMethod.Get, FilePath,
            DownloadPayloads.Streamed(HttpStatusCode.OK, new TestMediaStream(Source) { ChunkSize = 500, BreakAfter = 1500 }, 3000, "\"v1\""));

        var outcome = await RunAsync(Job());

        Assert.Equal(new TransferOutcome(TransferResult.Unreachable, "\"v1\"", null, 1500), outcome);
        Assert.Equal(Source[..1500], await PartAsync());
    }

    [Fact]
    public async Task ABodyLongerThanTheFile_StopsAtItsSize()
    {
        _silo.On(HttpMethod.Get, FilePath,
            DownloadPayloads.Streamed(HttpStatusCode.OK, new TestMediaStream(DownloadPayloads.Source(5000)) { ChunkSize = 1000 }, null, "\"v1\""));

        var outcome = await RunAsync(Job());

        Assert.Equal(TransferResult.Integrity, outcome.Result);
        Assert.InRange(new FileInfo(PartPath).Length, 0, 3000);
    }

    [Fact]
    public async Task A206LongerThanTheRest_StopsAtTheFilesSize()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        var body = new TestMediaStream(DownloadPayloads.Source(4000)) { ChunkSize = 1000 };
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Streamed(HttpStatusCode.PartialContent, body, 2000, "\"v1\"", 1000, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(TransferResult.Integrity, outcome.Result);
        Assert.InRange(new FileInfo(PartPath).Length, 1000, 3000);
    }

    [Fact]
    public async Task A206EndingPastTheFile_IsAnIntegrityFailure()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, DownloadPayloads.Source(3000), "\"v1\"", 1000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(TransferResult.Integrity, outcome.Result);
        Assert.Equal(Source[..1000], await PartAsync());
    }

    [Fact]
    public async Task AnUnknownLength_IsAnIntegrityFailure()
    {
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Streamed(HttpStatusCode.OK, new TestMediaStream(Source), null, "\"v1\""));

        var outcome = await RunAsync(Job(expectedBytes: 0));

        Assert.Equal(TransferResult.Integrity, outcome.Result);
        Assert.Equal(0, new FileInfo(PartPath).Length);
    }

    [Fact]
    public async Task AStalledBody_GivesUpAfterAMinute()
    {
        var body = new TestMediaStream(Source) { ChunkSize = 500, StallAfter = 1000 };
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Streamed(HttpStatusCode.OK, body, 3000, "\"v1\""));

        var run = RunAsync(Job());
        await body.Stalling.WaitAsync(Token);
        _time.Advance(TimeSpan.FromSeconds(59));

        Assert.False(run.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        var outcome = await run;

        Assert.Equal(new TransferOutcome(TransferResult.Unreachable, "\"v1\"", null, 1000), outcome);
        Assert.Equal(1000, new FileInfo(PartPath).Length);
    }

    public static TheoryData<int, string?, int?, TransferResult, int?> Answers => new()
    {
        { 409, ProblemCodes.PreparedFileExpired, null, TransferResult.Expired, null },
        { 409, ProblemCodes.Conflict, null, TransferResult.Refused, null },
        { 404, ProblemCodes.NotFound, null, TransferResult.Gone, null },
        { 429, ProblemCodes.RateLimited, 40, TransferResult.Busy, 40 },
        { 429, ProblemCodes.RateLimited, 0, TransferResult.Busy, 0 },
        { 503, ProblemCodes.DependencyUnavailable, null, TransferResult.Busy, null },
        { 500, null, null, TransferResult.Unreachable, null },
        { 401, ProblemCodes.SessionExpired, null, TransferResult.SignedOut, null },
        { 403, ProblemCodes.ProfileVerificationRequired, null, TransferResult.SignedOut, null },
        { 403, ProblemCodes.PermissionDenied, null, TransferResult.Refused, null },
        { 308, null, null, TransferResult.Refused, null },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task ServerAnswers_AreSortedOut(int status, string? code, int? retryAfterSeconds, TransferResult expected, int? waitSeconds)
    {
        var answer = code is null
            ? () => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))
            : ScriptedSilo.Problem(status, code, retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        _silo.On(HttpMethod.Get, FilePath, answer);

        var outcome = await RunAsync(Job());

        Assert.Equal(expected, outcome.Result);
        Assert.Equal(waitSeconds is { } wait ? TimeSpan.FromSeconds(wait) : null, outcome.RetryAfter);
    }

    [Fact]
    public async Task ALostConnection_IsUnreachable()
    {
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.NetworkError());

        Assert.Equal(TransferResult.Unreachable, (await RunAsync(Job())).Result);
    }

    [Fact]
    public async Task AnotherProfileActing_IsAProfileChange()
    {
        _silo.On(HttpMethod.Get, FilePath, () => Task.FromException<HttpResponseMessage>(new ProfileChangedException()));

        Assert.Equal(TransferResult.ProfileChanged, (await RunAsync(Job())).Result);
    }

    [Fact]
    public async Task AnIdTheApiRefuses_IsRefusedUnsent()
    {
        Assert.Equal(TransferResult.Refused, (await RunAsync(Job(id: ".."))).Result);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AFolderOnADriveThatIsGone_SaysMissing()
    {
        _disk = new FakeDiskSpace([null]);

        var outcome = await RunAsync(Job());

        Assert.Equal(TransferResult.FolderMissing, outcome.Result);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AFolderThatWasDeleted_SaysMissing()
    {
        Directory.Delete(_folder);

        var outcome = await RunAsync(Job());

        Assert.Equal(TransferResult.FolderMissing, outcome.Result);
        Assert.Empty(_silo.Sent);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task ALengthThatDoesNotMatch_IsAnIntegrityFailure()
    {
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.OK, Source, "\"v1\""));

        var outcome = await RunAsync(Job(expectedBytes: 4000));

        Assert.Equal(TransferResult.Integrity, outcome.Result);
    }

    [Fact]
    public async Task ARangeFromAnotherOffset_IsAnIntegrityFailure()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[500..], "\"v1\"", 500, 3000));

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        Assert.Equal(TransferResult.Integrity, outcome.Result);
        Assert.Equal(Source[..1000], await PartAsync());
    }

    [Fact]
    public async Task ARangePastAWholePart_IsCompleted()
    {
        await File.WriteAllBytesAsync(PartPath, Source, Token);
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.Problem(416, "range_not_satisfiable"));

        var outcome = await RunAsync(Job(resumeFrom: 3000, validator: "\"v1\""));

        Assert.Equal(new TransferOutcome(TransferResult.Completed, "\"v1\"", null, 3000), outcome);
        Assert.Equal(Source, await PartAsync());
    }

    [Fact]
    public async Task A416WithoutAKnownSize_IsAnIntegrityFailure()
    {
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.Problem(416, "range_not_satisfiable"));

        Assert.Equal(TransferResult.Integrity, (await RunAsync(Job(expectedBytes: 0, validator: "\"v1\""))).Result);
    }

    [Fact]
    public async Task APartThatCannotBeCutBack_IsALocalFailure_NotAnException()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        using var mapped = MappedPart();

        var outcome = await RunAsync(Job(resumeFrom: 500, validator: "\"v1\""));

        Assert.Equal(TransferResult.Unreachable, outcome.Result);
        Assert.Empty(_silo.Sent);
        Assert.Empty(_told);
    }

    [Fact]
    public async Task A206OfAnotherVersion_WhosePartCannotBeEmptied_IsStillToldNone()
    {
        await File.WriteAllBytesAsync(PartPath, Source[..1000], Token);
        _silo.On(HttpMethod.Get, FilePath, DownloadPayloads.Bytes(HttpStatusCode.PartialContent, Source[1000..], "\"v2\"", 1000, 3000));
        using var mapped = MappedPart();

        var outcome = await RunAsync(Job(resumeFrom: 1000, validator: "\"v1\""));

        // The bytes on disk may still be the old version's: without a validator, the next start begins again.
        Assert.Equal(TransferResult.Unreachable, outcome.Result);
        Assert.Equal([(null, 1000L)], _told);
    }

    // Another program has the part mapped into its memory: Windows refuses to cut its bytes off while it does.
    private Mapped MappedPart()
    {
        var stream = new FileStream(PartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var file = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
        return new Mapped(file, file.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read));
    }

    private sealed class Mapped(System.IO.MemoryMappedFiles.MemoryMappedFile file, System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view)
        : IDisposable
    {
        public void Dispose()
        {
            view.Dispose();
            file.Dispose();
        }
    }

    [Fact]
    public async Task NoAddressReachesTheLog()
    {
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.NetworkError());

        await RunAsync(Job());

        Assert.DoesNotContain("nas.example", _logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoLocalPathOrUnknownCodeReachesTheLog()
    {
        // A folder where the part belongs: it cannot be opened.
        Directory.CreateDirectory(PartPath);
        Assert.Equal(TransferResult.Unreachable, (await RunAsync(Job())).Result);
        Directory.Delete(PartPath);
        _silo.On(HttpMethod.Get, FilePath, ScriptedSilo.Problem(409, "made_up_code"));

        Assert.Equal(TransferResult.Refused, (await RunAsync(Job())).Result);

        Assert.NotEmpty(_logs.Lines);
        // The temporary folder is under the Windows user's own folder.
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("made_up_code", _logs.All, StringComparison.Ordinal);
    }
}
