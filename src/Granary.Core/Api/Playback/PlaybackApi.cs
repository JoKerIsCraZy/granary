namespace Granary.Core.Api.Playback;

/// <summary>
/// Playback protocol v3 on <c>/api/v2</c> (docs/api-contract.md, 8 and 10). Every call acts for the selected profile,
/// including the capabilities read. Problems arrive as <see cref="SiloApiException"/>; the ones a session has to
/// tell apart are <see cref="ProblemCodes.InstallationChanged"/> (409: refetch capabilities, start a new attempt),
/// <see cref="ProblemCodes.NotFound"/> (404: the session is gone, start a new attempt at the current position) and
/// <see cref="ProblemCodes.PlaybackSessionEnded"/> (410: an administrator ended it, leave playback).
/// </summary>
/// <remarks>
/// The checks made before sending catch client bugs that the server would answer with 422; they cover the rules
/// a caller is most likely to get wrong, not every bound the server enforces.
/// </remarks>
public sealed class PlaybackApi(SiloHttp http)
{
    private const int MaxAttemptCount = 8;
    private const int MaxAttemptedPlanKeys = 16;
    private const int MaxDiagnostics = 32;

    /// <summary>The installation id, features and deliveries; gate on <see cref="PlaybackCapabilities.IsUsable"/>.</summary>
    public Task<PlaybackCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/playback/capabilities", SiloAuth.Profile, PlaybackJsonContext.Default.PlaybackCapabilities, cancellationToken);

    /// <summary>
    /// Starts an attempt; the answer (201) is a plan or a terminal decision. Replaying an identical start returns
    /// the stored decision; the same attempt id with another body is 409 <c>idempotency_conflict</c>.
    /// </summary>
    public Task<PlaybackDecision> StartAsync(Uri server, PlaybackStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.StartPosition is { } start)
        {
            RequireSourceSeconds(start, nameof(request.StartPosition));
        }
        return http.PostAsync(server, "api/v2/playback/start", SiloAuth.Profile, request,
            PlaybackJsonContext.Default.PlaybackStartRequest, PlaybackJsonContext.Default.PlaybackDecision, cancellationToken);
    }

    /// <summary>
    /// Asks for a replacement plan. 409 <c>conflict</c> covers a replan still in flight, a stale <c>failed_plan_id</c>
    /// and a reused <c>replan_request_id</c> alike: branch on the status only.
    /// </summary>
    /// <exception cref="ArgumentException">The operation's failure rule, the attempt count or the key list is broken.</exception>
    public Task<PlaybackDecision> ReplanAsync(Uri server, string sessionId, PlaybackReplanRequest request, CancellationToken cancellationToken = default)
    {
        string path = SessionPath(sessionId) + "/replan";
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return http.PostAsync(server, path, SiloAuth.Profile, request,
            PlaybackJsonContext.Default.PlaybackReplanRequest, PlaybackJsonContext.Default.PlaybackDecision, cancellationToken);
    }

    /// <summary>
    /// Reports a sample, the session's heartbeat. A retry must resend the identical sample; the same sequence with
    /// another sample is 409 <c>progress_conflict</c>.
    /// </summary>
    public Task<PlaybackMutation> ReportProgressAsync(Uri server, string sessionId, PlaybackProgressRequest request, CancellationToken cancellationToken = default)
    {
        string path = SessionPath(sessionId) + "/progress";
        ArgumentNullException.ThrowIfNull(request);
        Validate(new PlaybackSample(request.Sequence, request.Position, request.IsPaused));
        return http.PostAsync(server, path, SiloAuth.Profile, request,
            PlaybackJsonContext.Default.PlaybackProgressRequest, PlaybackJsonContext.Default.PlaybackMutation, cancellationToken);
    }

    /// <summary>
    /// Stops the session: a <c>DELETE</c> with a JSON body and a JSON receipt. Every later stop, with any stop id,
    /// answers <c>replayed</c> with the stored receipt; 404 means the session row is unknown or expired.
    /// </summary>
    public Task<PlaybackMutation> StopAsync(Uri server, string sessionId, PlaybackStopRequest request, CancellationToken cancellationToken = default)
    {
        string path = SessionPath(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.FinalSample is { } sample)
        {
            Validate(sample);
        }
        return http.SendAsync(HttpMethod.Delete, server, path, SiloAuth.Profile, request,
            PlaybackJsonContext.Default.PlaybackStopRequest, PlaybackJsonContext.Default.PlaybackMutation, cancellationToken);
    }

    /// <summary>Records one diagnostic route event (202). Non-retryable: on 429 or 403 drop it.</summary>
    public Task<PlaybackRouteEventReceipt> SendRouteEventAsync(Uri server, PlaybackRouteEvent routeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeEvent);
        if (routeEvent.Diagnostics.Count > MaxDiagnostics)
        {
            throw new ArgumentException($"The server takes at most {MaxDiagnostics} diagnostics.", nameof(routeEvent));
        }
        return http.PostAsync(server, "api/v2/playback/route-events", SiloAuth.Profile, routeEvent,
            PlaybackJsonContext.Default.PlaybackRouteEvent, PlaybackJsonContext.Default.PlaybackRouteEventReceipt, cancellationToken);
    }

    private static string SessionPath(string sessionId) => "api/v2/playback/" + SiloHttp.PathSegment(sessionId, nameof(sessionId));

    private static void Validate(PlaybackReplanRequest request)
    {
        // A blank operation would fail the server's enum, and the server would read an absent one as failure_recovery.
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation, nameof(request));
        if (request.Operation == ReplanOperations.QualityChange && string.IsNullOrWhiteSpace(request.QualityPreference))
        {
            throw new ArgumentException("A quality_change replan needs the picked quality preference.", nameof(request));
        }
        if (ReplanOperations.NeedsFailure(request.Operation) && string.IsNullOrWhiteSpace(request.Failure?.Classification))
        {
            throw new ArgumentException($"A {request.Operation} replan needs a failure classification.", nameof(request));
        }
        if (ReplanOperations.ForbidsFailure(request.Operation) && request.Failure is not null)
        {
            throw new ArgumentException($"A {request.Operation} replan must not carry a failure.", nameof(request));
        }
        if (request.AttemptCount is < 1 or > MaxAttemptCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.AttemptCount, $"The attempt count must be between 1 and {MaxAttemptCount}.");
        }
        if (request.AttemptedPlanKeys.Count > MaxAttemptedPlanKeys)
        {
            throw new ArgumentException($"The server takes at most {MaxAttemptedPlanKeys} attempted plan keys.", nameof(request));
        }
        RequireSourceSeconds(request.PositionSeconds, nameof(request.PositionSeconds));
    }

    private static void Validate(PlaybackSample sample)
    {
        if (sample.Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), sample.Sequence, "A sample's sequence starts at 1.");
        }
        RequireSourceSeconds(sample.Position, nameof(sample.Position));
    }

    // Source seconds are finite and not negative; JSON has no NaN, and the server refuses negative positions.
    private static void RequireSourceSeconds(double seconds, string name)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(name, seconds, "A position must be a finite, non-negative number of seconds.");
        }
    }
}
