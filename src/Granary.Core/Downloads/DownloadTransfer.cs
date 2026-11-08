using System.Globalization;
using System.Net.Http.Headers;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// What a transfer is to do: download <see cref="DownloadId"/> of <see cref="Scope"/> into <see cref="DownloadPaths.MediaPart"/>
/// in <see cref="Folder"/>, carrying on from <see cref="ResumeFrom"/> with the <see cref="Validator"/> those bytes came with.
/// <see cref="ViaProxy"/> asks <c>/file-proxy</c>, which may send it on to a proxy node.
/// </summary>
/// <param name="ExpectedBytes">
/// The file's exact length: the manifest's <c>integrity.expected_bytes</c>, or the size of the entry once it is ready. Never the
/// size a preparing entry lists, which is an estimate: a transfer against it ends as an integrity failure.
/// </param>
public sealed record TransferJob(Uri Server, DownloadScope Scope, string DownloadId, string Folder, long ExpectedBytes, long ResumeFrom,
    string? Validator, bool ViaProxy);

/// <summary>How a transfer ended. Every result but <see cref="Completed"/> keeps the bytes it wrote.</summary>
public enum TransferResult
{
    /// <summary>The part is exactly the file's length, and on the disk.</summary>
    Completed,

    /// <summary>The drive has too little room for the rest, before the start or on the way.</summary>
    NotEnoughSpace,

    /// <summary>The folder or its drive is not there.</summary>
    FolderMissing,

    /// <summary>409 <c>prepared_file_expired</c>: the server cleaned up its prepared copy; it can prepare it again.</summary>
    Expired,

    /// <summary>
    /// The server will not serve it: revoked or not servable (409), not allowed (403, 4xx), or sent somewhere Granary does not
    /// follow (a redirect). Asking again gets the same answer.
    /// </summary>
    Refused,

    /// <summary>404: the entry or its title is gone, or outside the profile's access.</summary>
    Gone,

    /// <summary>429 or 503: the server asks to wait <see cref="TransferOutcome.RetryAfter"/>, or did not say how long (null).</summary>
    Busy,

    /// <summary>No answer, a lost or stalled connection, a server error, or a part of the file and no more: try again later.</summary>
    Unreachable,

    /// <summary>
    /// The file is not the one expected: another length, bytes past its end, a range from another offset or of another version.
    /// The part may have started over.
    /// </summary>
    Integrity,

    /// <summary>401, or the profile wants its PIN again: nothing goes on until the sign-in does.</summary>
    SignedOut,

    /// <summary>The server acts as another profile now; the request was not sent.</summary>
    ProfileChanged,
}

/// <summary>
/// A transfer's end: its <paramref name="Result"/>, the <paramref name="Validator"/> the bytes on disk came with (for the next
/// <c>If-Range</c>), the server's <c>Retry-After</c> for <see cref="TransferResult.Busy"/> (null when it named none), and the
/// bytes on disk.
/// </summary>
public sealed record TransferOutcome(TransferResult Result, string? Validator = null, TimeSpan? RetryAfter = null, long Bytes = 0);

/// <summary>
/// One media file over HTTP into <see cref="DownloadPaths.MediaPart"/> (docs/architecture.md, "Downloads"). A resume asks for the
/// rest with <c>Range</c> and <c>If-Range</c>; a server whose file changed answers 200, and the part starts over. Offsets and
/// sizes are <see cref="long"/> throughout, for files over 4 GB. The part is never deleted here.
/// </summary>
public sealed partial class DownloadTransfer(DownloadsApi api, IDiskSpace disk, TimeProvider time, ILogger<DownloadTransfer> logger)
{
    /// <summary>Room left free on the drive beside a download, for Windows and everything else on it.</summary>
    public const long SpaceReserve = 256L * 1024 * 1024;

    /// <summary>A connection that brings no byte for this long is given up, to be tried again.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 1024 * 1024;

    // The part is put on the disk at least this often: with 1 MiB reads, at most 4 MiB, the queue's resume margin after a
    // restart, are ever written but not on the disk.
    private const long FlushBytes = 3L * 1024 * 1024;

    // Windows' errors for a full drive, and for a drive that went away.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    private const int NotReady = unchecked((int)0x80070015);
    private const int DeviceNotConnected = unchecked((int)0x8007048F);

    // The problem codes the log may name; anything else a server sends is not written down.
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        ProblemCodes.AuthenticationRequired, ProblemCodes.InvalidToken, ProblemCodes.SessionExpired, ProblemCodes.TokenRefreshRequired,
        ProblemCodes.PermissionDenied, ProblemCodes.ProfileVerificationRequired, ProblemCodes.NotFound, ProblemCodes.Conflict,
        ProblemCodes.ValidationFailed, ProblemCodes.MalformedRequest, ProblemCodes.RateLimited, ProblemCodes.DependencyUnavailable,
        ProblemCodes.PreparedFileExpired, ProblemCodes.CapabilityUnsupported,
    };

    private readonly DownloadsApi _api = api;
    private readonly IDiskSpace _disk = disk;
    private readonly TimeProvider _time = time;
    private readonly ILogger<DownloadTransfer> _logger = logger;

    /// <summary>How many bytes are written between two looks at the free space.</summary>
    internal long SpaceCheckBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>For tests: runs after each time the part was put on the disk, with its length.</summary>
    internal Action<long>? Flushed { get; init; }

    /// <summary>
    /// Downloads the file, or the rest of it, into the part. Nothing is sent when the folder's drive is not there or, the size
    /// known, the rest from the offset actually used does not fit beside <see cref="SpaceReserve"/>.
    /// </summary>
    /// <param name="progress">Gets the bytes on disk after each write.</param>
    /// <param name="onValidator">
    /// Told the validator of the bytes in the part whenever it changes, before any of those bytes are written: the caller keeps
    /// it, so a transfer cancelled or killed meanwhile resumes with it. Null means the part's bytes have none, and a resume
    /// starts over. A part that starts over is emptied before the new validator is told.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the part keeps its bytes.</exception>
    public async Task<TransferOutcome> RunAsync(TransferJob job, Action<long> progress, Func<string?, CancellationToken, Task> onValidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(onValidator);
        if (_disk.FreeBytes(job.Folder) is not { } free)
        {
            return new TransferOutcome(TransferResult.FolderMissing, job.Validator);
        }
        FileStream part;
        try
        {
            part = new FileStream(Path.Combine(job.Folder, DownloadPaths.MediaPart), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferOutcome(LocalFailure(job, ex), job.Validator, Bytes: PartLength(job));
        }
        await using (part)
        {
            long from = ResumePoint(job, part.Length);
            if (job.ExpectedBytes > 0 && free - SpaceReserve < job.ExpectedBytes - from)
            {
                _logger.LogInformation("Download {DownloadId} does not fit on its drive", job.DownloadId);
                return new TransferOutcome(TransferResult.NotEnoughSpace, job.Validator, Bytes: part.Length);
            }
            using var stall = new CancellationTokenSource(StallTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
            var attempt = new Attempt(this, job, part, from, progress, onValidator, stall, linked.Token, cancellationToken);
            try
            {
                return await attempt.RunAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Download {DownloadId} got no byte for {Seconds} s", job.DownloadId, StallTimeout.TotalSeconds);
                return attempt.Outcome(TransferResult.Unreachable);
            }
        }
    }

    // Where the bytes on disk can be carried on from: within the part and the file, and only with a validator If-Range takes,
    // since without one a resume could join two versions' bytes.
    private static long ResumePoint(TransferJob job, long partLength)
    {
        if (!ByteRange.IsValidator(job.Validator))
        {
            return 0;
        }
        long end = job.ExpectedBytes > 0 ? Math.Min(partLength, job.ExpectedBytes) : partLength;
        return Math.Clamp(job.ResumeFrom, 0, end);
    }

    private static long PartLength(TransferJob job)
    {
        try
        {
            var part = new FileInfo(Path.Combine(job.Folder, DownloadPaths.MediaPart));
            return part.Exists ? part.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // A write or open of the part that failed: a full drive, a folder or drive that went, or anything else to try again.
    private TransferResult LocalFailure(TransferJob job, Exception ex)
    {
        switch (ex)
        {
            case DirectoryNotFoundException:
            case IOException { HResult: NotReady or DeviceNotConnected }:
                _logger.LogInformation("The folder of download {DownloadId} is not there", job.DownloadId);
                return TransferResult.FolderMissing;
            case IOException { HResult: DiskFull or HandleDiskFull }:
                _logger.LogInformation("The drive of download {DownloadId} is full", job.DownloadId);
                return TransferResult.NotEnoughSpace;
            default:
                // The message names the full path, with the Windows user name: only the kind and code are logged.
                _logger.LogWarning("The file of download {DownloadId} could not be written ({Failure})", job.DownloadId, DownloadLog.Failure(ex));
                return TransferResult.Unreachable;
        }
    }

    // A strong ETag, else Last-Modified in its HTTP form: what If-Range takes.
    private static string? ValidatorOf(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } etag
            ? etag.ToString()
            : response.Content.Headers.LastModified?.ToString("r", CultureInfo.InvariantCulture);

    // A 206's bytes belong to the version on disk: its ETag, or else its Last-Modified, is the validator's, or it names none.
    private static bool IsSameVersion(HttpResponseMessage response, string? validator)
    {
        if (!RangeConditionHeaderValue.TryParse(validator, out var condition))
        {
            return false;
        }
        if (condition.EntityTag is { } tag)
        {
            return response.Headers.ETag is not { } etag || (!etag.IsWeak && etag.Tag == tag.Tag);
        }
        return response.Content.Headers.LastModified is not { } modified || modified == condition.Date;
    }
}
