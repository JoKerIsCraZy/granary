using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

public enum ProgressOutcome
{
    /// <summary>The server has the sample (applied, replayed, or a newer one it already had).</summary>
    Reported,

    /// <summary>Not delivered for now (network, server busy, or the session is stopping); the next sample tries again.</summary>
    Unreported,

    /// <summary>404: the session expired or the server forgot it. A viewer still watching needs a new attempt.</summary>
    SessionGone,

    /// <summary>410 or 403: the session was ended for this viewer (an administrator's stop). Leave playback.</summary>
    SessionEnded,

    /// <summary>409 <c>installation_changed</c>: the server was reinstalled; fetch its capabilities again and start anew.</summary>
    InstallationChanged,
}

/// <summary>Delays and budgets of <see cref="PlaybackProgress"/>; tests shorten them.</summary>
/// <param name="ProgressRetryDelay">Between the tries of one sample.</param>
/// <param name="StopRetryDelay">Between the tries of the stop.</param>
/// <param name="StopBudget">How long the stop keeps trying, and how long it waits for a sample still on its way.</param>
public sealed record PlaybackRetryPolicy(TimeSpan ProgressRetryDelay, TimeSpan StopRetryDelay, TimeSpan StopBudget)
{
    public static PlaybackRetryPolicy Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30));
}

/// <summary>
/// The progress and stop of one server session (docs/api-contract.md, 10.1 to 10.3), as Silo's web player sends
/// them: one sequence per sample, samples one after another, a lost answer resent with the identical body, and a
/// stop with one <c>stop_id</c> and the final sample, kept identical across retries until the server has it. The
/// sequence belongs to the session and continues across its replans.
/// </summary>
public sealed class PlaybackProgress(
    PlaybackApi api,
    Uri server,
    string sessionId,
    string installationId,
    ILogger<PlaybackProgress> logger,
    PlaybackRetryPolicy? policy = null)
{
    private const int ProgressTries = 3;

    private readonly PlaybackRetryPolicy _policy = policy ?? PlaybackRetryPolicy.Default;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly Lock _gate = new();
    private long _sequence;
    private Task? _stop;

    public string SessionId => sessionId;

    /// <summary>The stop reached the server, or the server no longer knew the session.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>Reports <paramref name="sourcePosition"/>; the outcome says what the server thinks of the session.</summary>
    public async Task<ProgressOutcome> ReportAsync(double sourcePosition, bool isPaused)
    {
        if (IsStopping)
        {
            return ProgressOutcome.Unreported;
        }
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                return ProgressOutcome.Unreported;
            }
            var sample = new PlaybackSample(++_sequence, Clamp(sourcePosition), isPaused);
            return await SendAsync(sample).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Ends the session with the final position. The first call stops; later calls wait for the same stop.</summary>
    public Task StopAsync(double sourcePosition, bool isPaused)
    {
        lock (_gate)
        {
            return _stop ??= StopOnceAsync(Clamp(sourcePosition), isPaused);
        }
    }

    private bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stop is not null;
            }
        }
    }

    private async Task<ProgressOutcome> SendAsync(PlaybackSample sample)
    {
        var request = PlaybackProgressRequest.For(installationId, sample);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var receipt = await api.ReportProgressAsync(server, sessionId, request).ConfigureAwait(false);
                if (receipt.Accepted is { } accepted && accepted.Sequence > _sequence)
                {
                    _sequence = accepted.Sequence;
                }
                return ProgressOutcome.Reported;
            }
            catch (SiloApiException ex) when (SessionOutcome(ex) is { } outcome)
            {
                logger.LogInformation("Progress of session {SessionId} answered {Status} {Code}", sessionId, ex.Status, ex.Code);
                return outcome;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < ProgressTries)
            {
                await Task.Delay(_policy.ProgressRetryDelay).ConfigureAwait(false);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                logger.LogWarning("Progress of session {SessionId} was not delivered: {Failure}", sessionId, ApiFailureText.Describe(ex));
                return ProgressOutcome.Unreported;
            }
        }
    }

    private async Task StopOnceAsync(double position, bool isPaused)
    {
        // A sample still on its way must land first: the final one has to come after it.
        bool entered = await _sending.WaitAsync(_policy.StopBudget).ConfigureAwait(false);
        try
        {
            var request = new PlaybackStopRequest
            {
                InstallationId = installationId,
                StopId = Guid.NewGuid().ToString(),
                FinalSample = new PlaybackSample(++_sequence, position, isPaused),
            };
            var deadline = DateTime.UtcNow + _policy.StopBudget;
            while (true)
            {
                try
                {
                    await api.StopAsync(server, sessionId, request).ConfigureAwait(false);
                    break;
                }
                catch (SiloApiException ex) when (ex is { Status: 404 } or { Status: 409, Code: ProblemCodes.InstallationChanged })
                {
                    // Expired, never durable, or on a reinstalled server that no longer knows it: nothing left to stop.
                    break;
                }
                catch (Exception ex) when (IsTransient(ex) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_policy.StopRetryDelay).ConfigureAwait(false);
                }
                catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
                {
                    logger.LogWarning("Stopping session {SessionId} failed: {Failure}", sessionId, ApiFailureText.Describe(ex));
                    return;
                }
            }
            IsStopped = true;
            logger.LogInformation("Session {SessionId} stopped at {Position:F1} s", sessionId, position);
        }
        finally
        {
            if (entered)
            {
                _sending.Release();
            }
        }
    }

    private static ProgressOutcome? SessionOutcome(SiloApiException ex) => ex switch
    {
        { Status: 404 } => ProgressOutcome.SessionGone,
        { Status: 410 or 403 } => ProgressOutcome.SessionEnded,
        { Status: 409, Code: ProblemCodes.InstallationChanged } => ProgressOutcome.InstallationChanged,
        _ => null,
    };

    // Worth repeating with the identical body: the network, a busy or restarting server, a timeout.
    internal static bool IsTransient(Exception ex) => ex switch
    {
        SiloApiException api => api.Status is 408 or 429 or >= 500,
        HttpRequestException => true,
        OperationCanceledException => true,
        _ => false,
    };

    private static double Clamp(double position) => double.IsFinite(position) && position > 0 ? position : 0;
}
