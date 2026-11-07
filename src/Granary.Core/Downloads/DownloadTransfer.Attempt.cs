using System.Buffers;
using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

public sealed partial class DownloadTransfer
{
    /// <summary>One request and its body, into the part opened for it, from <paramref name="from"/>.</summary>
    private sealed class Attempt(DownloadTransfer owner, TransferJob job, FileStream part, long from, Action<long> progress,
        Func<string?, CancellationToken, Task> onValidator, CancellationTokenSource stall, CancellationToken token, CancellationToken callerToken)
    {
        // The validator of the bytes in the part, as the caller was last told or had it.
        private string? _validator;

        public TransferOutcome Outcome(TransferResult result, TimeSpan? retryAfter = null) => new(result, _validator, retryAfter, part.Length);

        public async Task<TransferOutcome> RunAsync()
        {
            _validator = ByteRange.IsValidator(job.Validator) ? job.Validator : null;
            if (CutTo(from) is { } failed)
            {
                return failed;
            }
            var range = from > 0 ? new ByteRange(from, job.Validator) : null;
            stall.CancelAfter(StallTimeout);
            HttpResponseMessage response;
            try
            {
                response = await owner._api.OpenFileAsync(job.Server, job.Scope.ProfileId, job.DownloadId, range, job.ViaProxy, token)
                    .ConfigureAwait(false);
            }
            catch (ProfileChangedException)
            {
                return Outcome(TransferResult.ProfileChanged);
            }
            catch (DownloadRedirectRefusedException)
            {
                owner._logger.LogInformation("Download {DownloadId} was sent to an address Granary does not follow", job.DownloadId);
                return Outcome(TransferResult.Refused);
            }
            catch (HttpRequestException ex)
            {
                // The exception's message can name the server: only its kind is logged.
                owner._logger.LogInformation("Download {DownloadId} could not reach the server ({Error})", job.DownloadId, ex.HttpRequestError);
                return Outcome(TransferResult.Unreachable);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                owner._logger.LogWarning("Download {DownloadId} cannot be asked for ({Failure})", job.DownloadId, DownloadLog.Failure(ex));
                return Outcome(TransferResult.Refused);
            }
            using (response)
            {
                return response.StatusCode switch
                {
                    HttpStatusCode.OK => await CopyAsync(response, 0).ConfigureAwait(false),
                    HttpStatusCode.PartialContent => await ResumeAsync(response).ConfigureAwait(false),
                    HttpStatusCode.RequestedRangeNotSatisfiable when job.ExpectedBytes > 0 && part.Length == job.ExpectedBytes => Done(),
                    HttpStatusCode.RequestedRangeNotSatisfiable => Outcome(TransferResult.Integrity),
                    _ => await RefusedAsync(response).ConfigureAwait(false),
                };
            }
        }

        // A 206 must carry on exactly where the part ends, within the file, with the bytes of its version.
        private async Task<TransferOutcome> ResumeAsync(HttpResponseMessage response)
        {
            var range = response.Content.Headers.ContentRange;
            long? expected = job.ExpectedBytes > 0 ? job.ExpectedBytes : range?.Length;
            long? length = response.Content.Headers.ContentLength;
            if (range is not { HasRange: true, From: { } start, To: { } end } || range.Unit != "bytes" || start != from || end < start
                || (expected is { } size && end >= size) || (length is { } count && count != end - start + 1))
            {
                owner._logger.LogWarning("Download {DownloadId} got bytes from another offset or past the file's end", job.DownloadId);
                return Outcome(TransferResult.Integrity);
            }
            if (!IsSameVersion(response, job.Validator))
            {
                // The rest of another version of the file: the bytes on disk start over, and have no validator until then, also
                // when they cannot be cut off now.
                owner._logger.LogWarning("Download {DownloadId} got the rest of another version of its file; it starts over", job.DownloadId);
                var failed = CutTo(0);
                await TellAsync(null).ConfigureAwait(false);
                return failed ?? Outcome(TransferResult.Integrity);
            }
            return await CopyAsync(response, from).ConfigureAwait(false);
        }

        private async Task<TransferOutcome> CopyAsync(HttpResponseMessage response, long start)
        {
            long? total = start == 0 ? response.Content.Headers.ContentLength : response.Content.Headers.ContentRange?.Length;
            if ((job.ExpectedBytes > 0 ? job.ExpectedBytes : total) is not { } expected)
            {
                owner._logger.LogWarning("Download {DownloadId} has no known length", job.DownloadId);
                return Outcome(TransferResult.Integrity);
            }
            if (total is { } size && size != expected)
            {
                owner._logger.LogWarning("Download {DownloadId} is {Size} bytes on the server, not {Expected}", job.DownloadId, size, expected);
                return Outcome(TransferResult.Integrity);
            }
            if (start == 0)
            {
                // The file changed on the server, or there was nothing to resume: the part starts over, emptied before the new
                // validator is told, so no crash can pair it with the old bytes. Bytes that cannot be cut off keep their own.
                if (CutTo(0) is { } failed)
                {
                    return failed;
                }
                await TellAsync(ValidatorOf(response)).ConfigureAwait(false);
            }
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                if (await PumpAsync(body, buffer, expected).ConfigureAwait(false) is { } stopped)
                {
                    return stopped;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            // A body that ended before the file did (a part of it, or a connection closed early) carries on later.
            return part.Length == expected ? Done() : Outcome(TransferResult.Unreachable);
        }

        // The body into the part until it ends; an outcome when it stopped before.
        private async Task<TransferOutcome?> PumpAsync(Stream body, byte[] buffer, long expected)
        {
            long sinceCheck = 0;
            long sinceFlush = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    owner._logger.LogInformation("Download {DownloadId} lost its connection after {Bytes} bytes ({Error})", job.DownloadId, part.Length,
                        ex.GetType().Name);
                    return Outcome(TransferResult.Unreachable);
                }
                if (read == 0)
                {
                    return null;
                }
                if (part.Position + read > expected)
                {
                    owner._logger.LogWarning("Download {DownloadId} got more bytes than its file has", job.DownloadId);
                    return Outcome(TransferResult.Integrity);
                }
                try
                {
                    await part.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Outcome(owner.LocalFailure(job, ex));
                }
                progress(part.Position);
                sinceFlush += read;
                if (sinceFlush >= FlushBytes)
                {
                    sinceFlush = 0;
                    if (FlushToDisk() is { } failed)
                    {
                        return failed;
                    }
                }
                sinceCheck += read;
                if (sinceCheck >= owner.SpaceCheckBytes)
                {
                    sinceCheck = 0;
                    if (SpaceLeft() is { } stopped)
                    {
                        return stopped;
                    }
                }
            }
        }

        // The part cut back to length and the next write placed there; a part the drive or another program does not let go of
        // is a local failure, not an exception.
        private TransferOutcome? CutTo(long length)
        {
            try
            {
                if (part.Length != length)
                {
                    part.SetLength(length);
                }
                part.Position = length;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Outcome(owner.LocalFailure(job, ex));
            }
        }

        // Whole, and on the disk before it counts as done.
        private TransferOutcome Done() => FlushToDisk() ?? Outcome(TransferResult.Completed);

        private TransferOutcome? FlushToDisk()
        {
            try
            {
                part.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Outcome(owner.LocalFailure(job, ex));
            }
            owner.Flushed?.Invoke(part.Length);
            return null;
        }

        private async Task TellAsync(string? validator)
        {
            if (validator != _validator || validator != job.Validator)
            {
                await onValidator(validator, callerToken).ConfigureAwait(false);
            }
            _validator = validator;
        }

        private TransferOutcome? SpaceLeft()
        {
            switch (owner._disk.FreeBytes(job.Folder))
            {
                case null:
                    return Outcome(TransferResult.FolderMissing);
                case < SpaceReserve:
                    owner._logger.LogInformation("The drive of download {DownloadId} is almost full; it stops", job.DownloadId);
                    return Outcome(TransferResult.NotEnoughSpace);
                default:
                    return null;
            }
        }

        // An answer that brings no bytes, by its status and problem code.
        private async Task<TransferOutcome> RefusedAsync(HttpResponseMessage response)
        {
            int status = (int)response.StatusCode;
            string? code = status is 403 or 409 ? (await SiloHttp.ReadProblemAsync(response, token).ConfigureAwait(false))?.Code : null;
            owner._logger.LogInformation("Download {DownloadId} was answered {Status} {Code}", job.DownloadId, status,
                code is not null && KnownCodes.Contains(code) ? code : "");
            return status switch
            {
                401 => Outcome(TransferResult.SignedOut),
                403 when code == ProblemCodes.ProfileVerificationRequired => Outcome(TransferResult.SignedOut),
                404 => Outcome(TransferResult.Gone),
                409 when code == ProblemCodes.PreparedFileExpired => Outcome(TransferResult.Expired),
                429 or 503 => Outcome(TransferResult.Busy, SiloHttp.RetryAfter(response) ?? BusyWait),
                >= 500 => Outcome(TransferResult.Unreachable),
                _ => Outcome(TransferResult.Refused),
            };
        }
    }
}
