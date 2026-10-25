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
    // Silo's web player waits the same way).
    private static readonly TimeSpan[] ReconnectDelays =
    [
        .. new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s)),
        .. Enumerable.Repeat(TimeSpan.FromSeconds(15), 8),
    ];

    private readonly List<string> _failedPlanKeys = [];
    private int _authReloads;
    private bool _reconnecting;

    private void ResetPlanChain() => _failedPlanKeys.Clear();

    // The headers expired or were revoked mid-stream: the same plan plays on with fresh ones from where it was (9.4).
    private async Task ReloadAfterRejectionAsync()
    {
        if (++_authReloads > MaxAuthReloads)
        {
            Fail("The server keeps rejecting the sign-in. Try again in a moment.", canRetry: true);
            return;
        }
        _logger.LogInformation("The server rejected the media headers; reloading with fresh ones");
        double position = _started ? _playerPosition : _plan!.Timeline.PlayerStartSeconds;
        await LoadAsync(position, rejected: true);
    }

    // The plan never played (a route or decoder failure): ask for another one, with every key tried so far.
    private async Task RecoverFromFailureAsync(string classification, string? detail)
    {
        var plan = _plan!;
        _failedPlanKeys.Add(plan.PlanAttemptKey);
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
            _playerPosition = _plan!.Timeline.ToPlayer(sourceSeconds);
            await AdoptAsync(decision, newSession: false, restorePosition: sourceSeconds);
        }
    }

    // A connection lost after playback began is not a failed route (8.7, protocol 6.2): keep the position and ask
    // the session for a plan again, a no-op track change, until the server answers.
    private async Task ReconnectAsync()
    {
        if (_reconnecting)
        {
            return;
        }
        _reconnecting = true;
        int generation = ++_generation;
        double position = SourcePosition;
        _logger.LogInformation("The stream broke off at {Position:F1} s; reconnecting", position);
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        foreach (var delay in ReconnectDelays)
        {
            await Task.Delay(delay, _time);
            if (_stopping || generation != _generation)
            {
                return;
            }
            PlaybackDecision decision;
            try
            {
                var request = PlaybackStarter.Replan(_context!, _attemptId, _plan!, _planAttemptId, ReplanOperations.TrackChange, position, []);
                decision = await _api.ReplanAsync(_context!.Server.BaseUri, _progress!.SessionId, request);
            }
            catch (SiloApiException ex) when (ex.Status == 410)
            {
                EndedByServer();
                return;
            }
            catch (Exception ex) when (PlaybackProgress.IsTransient(ex))
            {
                // Still unreachable, busy or restarting: wait for the next try.
                continue;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                // Any other answer: the session did not survive (404 after a server restart, 409, 403, 422).
                _logger.LogInformation("The session did not survive the outage ({Failure}); starting again", ApiFailureText.Describe(ex));
                if (ex is SiloApiException { Code: ProblemCodes.InstallationChanged })
                {
                    _context = await _starter.WithFreshCapabilitiesAsync(_context!, CancellationToken.None);
                }
                await RestartAsync(position);
                return;
            }
            if (_stopping || generation != _generation)
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
        _reconnecting = false;
        Fail("The connection to the server was lost.", canRetry: true);
    }

    // The session is gone: a new attempt at the viewer's position, with the same file.
    private async Task RestartAsync(double? position = null)
    {
        double at = position ?? SourcePosition;
        _logger.LogInformation("Starting a new attempt at {Position:F1} s", at);
        StopProgressTimer();
        _reconnecting = false;
        await StartAttemptAsync(at);
    }

    // An administrator ended the session: leave, do not start again (10.1).
    private void EndedByServer()
    {
        _engine.Stop();
        Fail("This playback was ended on the server.", canRetry: false);
    }

    // Asks the session for a new plan; null when it cannot give one (and the playback says why or starts anew).
    private async Task<PlaybackDecision?> ReplanAsync(string operation, double position, IReadOnlyList<string> attemptedKeys, PlaybackFailure? failure = null)
    {
        int generation = _generation;
        var request = PlaybackStarter.Replan(_context!, _attemptId, _plan!, _planAttemptId, operation, position, attemptedKeys, failure);
        try
        {
            var decision = await _api.ReplanAsync(_context!.Server.BaseUri, _progress!.SessionId, request);
            return _stopping || generation != _generation ? null : decision;
        }
        catch (SiloApiException ex) when (ex.Status == 410)
        {
            EndedByServer();
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            await RestartAsync(position);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Replan ({Operation}) failed: {Failure}", operation, ApiFailureText.Describe(ex));
            var described = PlaybackStarter.Describe(ex);
            Fail(described.Message, described.CanRetry);
        }
        return null;
    }

    // Within the produced window, or with an open window, between its start and where the viewer is.
    private bool IsInsideStream(PlaybackTimeline timeline, double sourceSeconds)
    {
        double start = timeline.SeekWindowStartSeconds ?? timeline.StreamOriginSeconds;
        double end = timeline.SeekWindowEndSeconds ?? SourcePosition;
        return sourceSeconds >= start && sourceSeconds <= end;
    }
}
