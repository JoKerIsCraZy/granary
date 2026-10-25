using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// What the protocol lets a client recover (docs/api-contract.md, 8.7, 9.4, 9.5, 10.1).
public sealed partial class ServerPlayback
{
    // The server keeps no history of failed plans and does not stop a chain itself (8.7).
    private const int MaxPlanAttempts = 8;

    // Rejected headers are renewed and the stream reloaded; this many in a row without the stream starting is a
    // server that keeps refusing, not an expired token.
    private const int MaxAuthReloads = 2;

    // After a lost connection: 1, 2, 4, 8 s, then every 15 s, for about two and a half minutes (protocol 6.2;
    // Silo's web player waits the same way). The count goes on while reloads fail again, until a frame shows.
    private static readonly TimeSpan[] ReconnectDelays =
    [
        .. new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s)),
        .. Enumerable.Repeat(TimeSpan.FromSeconds(15), 8),
    ];

    // A replan that met a busy or unreachable server goes again, unchanged, after these (Android: 1, 2, 4, 8 s).
    private static readonly TimeSpan[] ReplanRetryDelays = [.. new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s))];

    private readonly List<string> _failedPlanKeys = [];

    // Once per chain: the server answers sourceRefused with the same plan through its own address, so a second one
    // would be a node it keeps choosing, and counts as an ordinary failure.
    private bool _sourceRefused;
    private int _authReloads;
    private int _reconnectTries;
    private bool _recovering;
    private double? _pendingSeek;

    /// <summary>The latest recovery; tests wait for it instead of for time.</summary>
    internal Task Recovery { get; private set; } = Task.CompletedTask;

    private void ResetPlanChain()
    {
        _failedPlanKeys.Clear();
        _sourceRefused = false;
    }

    private bool Superseded(int generation) => _stopping || generation != _generation;

    // One recovery at a time: the server refuses a second replan of the session while one is open (8.7). What
    // happens meanwhile belongs to the stream being replaced, or happens again once the new one plays; a seek waits.
    private void Recover(Func<Task> recovery)
    {
        if (_recovering || _stopping || Status.Phase == ServerPlaybackPhase.Failed)
        {
            return;
        }
        _recovering = true;
        Recovery = Guarded(async () =>
        {
            try
            {
                await recovery();
            }
            finally
            {
                _recovering = false;
            }
        });
    }

    // The headers expired or were revoked mid-stream: the same plan plays on with fresh ones from where it was (9.4).
    private async Task ReloadAfterRejectionAsync()
    {
        if (++_authReloads > MaxAuthReloads)
        {
            Fail("The server keeps rejecting the sign-in. Try again in a moment.", canRetry: true);
            return;
        }
        _logger.LogInformation("The server rejected the media headers; reloading with fresh ones");
        // The last frame shown, a seek's target, or the start of a load that never played.
        await LoadAsync(_playerPosition, rejected: true);
    }

    // The plan never played (a route or decoder failure): ask for another one, with every key tried so far.
    private async Task RecoverFromFailureAsync(string classification, string? detail)
    {
        var plan = _plan!;
        if (classification == FailureClassifications.SourceRefused && _sourceRefused)
        {
            classification = "load_failed";
        }
        if (classification == FailureClassifications.SourceRefused)
        {
            // Not a failed recipe: the server replays this plan through the API and does not count it either.
            _sourceRefused = true;
        }
        else
        {
            _failedPlanKeys.Add(plan.PlanAttemptKey);
        }
        if (_failedPlanKeys.Count > MaxPlanAttempts)
        {
            Fail("This title could not be played. Try again later.", canRetry: true);
            return;
        }
        _logger.LogWarning("The {Delivery} plan failed ({Classification}); asking for another", plan.Delivery, classification);
        SendRouteEvent(RouteEvents.PlanFailed, failure: classification);
        var failure = new PlaybackFailure
        {
            Classification = classification,
            Message = detail is { Length: > 512 } ? detail[..512] : detail,
        };
        double position = SourcePosition;
        var decision = await ReplanAsync(ReplanOperations.FailureRecovery, position, [.. _failedPlanKeys], failure);
        if (decision is not null)
        {
            await AdoptAsync(decision, newSession: false, restorePosition: position);
        }
    }

    // A seek outside what the stream holds: the server re-anchors the stream at the target (8.6).
    private async Task ReanchorAsync(double sourceSeconds)
    {
        ResetPlanChain();
        var decision = await ReplanAsync(ReplanOperations.SeekReanchor, sourceSeconds, []);
        if (decision is not null)
        {
            await AdoptAsync(decision, newSession: false, restorePosition: sourceSeconds);
        }
    }

    // A connection lost after playback began is not a failed route (8.7, protocol 6.2): keep the position and ask
    // the session for a plan again, a no-op track change, until the server answers.
    private async Task ReconnectAsync()
    {
        int generation = _generation;
        double position = SourcePosition;
        _logger.LogInformation("The stream broke off at {Position:F1} s; reconnecting", position);
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        // One request, sent again unchanged until the server answers it (8.7).
        var request = PlaybackStarter.Replan(_context!, _attemptId, _plan!, _planAttemptId, ReplanOperations.TrackChange, position, []);
        while (_reconnectTries < ReconnectDelays.Length)
        {
            await Task.Delay(ReconnectDelays[_reconnectTries++], _time);
            if (Superseded(generation))
            {
                return;
            }
            PlaybackDecision decision;
            try
            {
                decision = await _api.ReplanAsync(_context!.Server.BaseUri, _progress!.SessionId, request);
            }
            catch (Exception ex) when (PlaybackProgress.IsTransient(ex))
            {
                // Still unreachable, busy or restarting: wait for the next try.
                continue;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                if (Superseded(generation))
                {
                    return;
                }
                if (ex is SiloApiException { Status: 410 })
                {
                    EndedByServer();
                    return;
                }
                // Any other answer: the session did not survive (404 after a server restart, 409, 403, 422).
                _logger.LogInformation("The session did not survive the outage ({Failure}); starting again", ApiFailureText.Describe(ex));
                await RestartAfterAsync(ex, position);
                return;
            }
            if (Superseded(generation))
            {
                return;
            }
            if (!decision.IsPlayable)
            {
                await RestartAsync(position);
                return;
            }
            ResetPlanChain();
            await AdoptAsync(decision, newSession: false, restorePosition: position);
            return;
        }
        Fail("The connection to the server was lost.", canRetry: true);
    }

    // The session is gone: a new attempt at the viewer's position, with the same file.
    private async Task RestartAsync(double? position = null)
    {
        double at = position ?? SourcePosition;
        _logger.LogInformation("Starting a new attempt at {Position:F1} s", at);
        StopTimers();
        _starting = Guarded(() => StartAttemptAsync(at));
        await _starting;
    }

    // A new attempt after the session refused to go on, with fresh capabilities if the server was reinstalled.
    private async Task RestartAfterAsync(Exception refusal, double position)
    {
        if (refusal is SiloApiException { Code: ProblemCodes.InstallationChanged } && !await RefreshCapabilitiesAsync())
        {
            return;
        }
        await RestartAsync(position);
    }

    // An administrator ended the session: leave, do not start again (10.1).
    private void EndedByServer() => Fail("This playback was ended on the server.", canRetry: false);

    // Asks the session for a new plan; null when it cannot give one (and the playback says why or starts anew).
    private async Task<PlaybackDecision?> ReplanAsync(string operation, double position, IReadOnlyList<string> attemptedKeys, PlaybackFailure? failure = null)
    {
        int generation = _generation;
        var request = PlaybackStarter.Replan(_context!, _attemptId, _plan!, _planAttemptId, operation, position, attemptedKeys, failure);
        for (int retry = 0; ; retry++)
        {
            Exception refusal;
            try
            {
                var decision = await _api.ReplanAsync(_context!.Server.BaseUri, _progress!.SessionId, request);
                return Superseded(generation) ? null : decision;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                refusal = ex;
            }
            if (Superseded(generation))
            {
                return null;
            }
            switch (refusal)
            {
                case SiloApiException { Status: 410 }:
                    EndedByServer();
                    return null;
                case SiloApiException { Status: 404 } or SiloApiException { Status: 409, Code: ProblemCodes.InstallationChanged }:
                    await RestartAfterAsync(refusal, position);
                    return null;
            }
            // 409 is also an identical replan still in flight: like a busy server, it gets the same body again.
            bool conflict = refusal is SiloApiException { Status: 409 };
            if ((conflict || PlaybackProgress.IsTransient(refusal)) && retry < ReplanRetryDelays.Length)
            {
                _logger.LogInformation("Replan ({Operation}) was not answered ({Failure}); sending it again", operation, ApiFailureText.Describe(refusal));
                await Task.Delay(ReplanRetryDelays[retry], _time);
                if (Superseded(generation))
                {
                    return null;
                }
                continue;
            }
            _logger.LogWarning("Replan ({Operation}) failed: {Failure}", operation, ApiFailureText.Describe(refusal));
            if (conflict)
            {
                // Still refused: the session's plan is not the one this playback knows. Start over where the viewer is.
                await RestartAsync(position);
                return null;
            }
            var described = PlaybackStarter.Describe(refusal);
            Fail(described.Message, described.CanRetry);
            return null;
        }
    }

    // Within the produced window, or with an open window, between its start and where the viewer is.
    private bool IsInsideStream(PlaybackTimeline timeline, double sourceSeconds)
    {
        double start = timeline.SeekWindowStartSeconds ?? timeline.StreamOriginSeconds;
        double end = timeline.SeekWindowEndSeconds ?? SourcePosition;
        return sourceSeconds >= start && sourceSeconds <= end;
    }
}
