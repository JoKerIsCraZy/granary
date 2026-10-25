using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

public sealed record ServerPlaybackOptions(PlaybackRetryPolicy RetryPolicy)
{
    public static ServerPlaybackOptions Default { get; } = new(PlaybackRetryPolicy.Default);
}

/// <summary>
/// One playback of a server title (docs/api-contract.md, 8 to 10), from <see cref="StartAsync"/> to <see cref="StopAsync"/>.
/// It adopts the server's plan, loads it into the engine with the media headers at the plan's start, reports
/// progress every 10 seconds and at every pause, and recovers what the protocol lets a client recover: a rejected
/// token, a plan that will not load, a lost connection, a session the server forgot, a seek outside the stream.
/// </summary>
/// <remarks>
/// Used on the UI thread: engine events and the progress timer are posted there, so no state is shared across
/// threads. Calls and answers that belong to a load that was replaced meanwhile are dropped by
/// <see cref="_generation"/>.
/// </remarks>
public sealed partial class ServerPlayback : IServerPlayback
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    // mpv reports a stream that broke off as its end: an end earlier than this before the title's end is a lost
    // connection, not the end of the title.
    private const double EndTolerance = 30;

    private readonly PlaybackStarter _starter;
    private readonly PlaybackApi _api;
    private readonly IMediaAuthorization _auth;
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly ServerPlaybackOptions _options;

    private PlaybackContext? _context;
    private string _attemptId = "";
    private PlaybackPlan? _plan;
    private string _planAttemptId = "";
    private PlaybackProgress? _progress;
    private Uri? _streamUrl;
    private ITimer? _progressTimer;
    private double _playerPosition;
    private bool _paused;
    private bool _started;
    private bool _firstFrameReported;
    private long _loadedAt;
    private bool _stopping;
    private Task _stop = Task.CompletedTask;
    private int _generation;

    public ServerPlayback(
        PlaybackStarter starter,
        PlaybackApi api,
        IMediaAuthorization auth,
        IPlaybackEngine engine,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILoggerFactory loggers,
        ServerPlaybackOptions? options = null)
    {
        _starter = starter;
        _api = api;
        _auth = auth;
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ServerPlayback>();
        _options = options ?? ServerPlaybackOptions.Default;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Ended += OnEngineEnded;
    }

    public event EventHandler<ServerPlaybackStatus>? StatusChanged;

    public ServerPlaybackStatus Status { get; private set; } = new(ServerPlaybackPhase.Preparing);

    public string Title { get; private set; } = "";

    public string Subtitle { get; private set; } = "";

    public double TimelineOffsetSeconds => _plan?.Timeline.TimelineOffsetSeconds ?? 0;

    public double? DurationSeconds =>
        _plan?.Source.DurationSeconds ?? (_context?.Version.DurationSeconds is > 0 and var seconds ? seconds : null);

    public PlaybackPreferences Preferences => _context?.Preferences ?? PlaybackPreferences.Default;

    // Where the viewer is in the title.
    private double SourcePosition => _plan?.Timeline.ToSource(_playerPosition) ?? _playerPosition;

    public async Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentId is not { } contentId)
        {
            throw new ArgumentException("A server playback plays a server title.", nameof(request));
        }
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        try
        {
            _context = await _starter.PrepareAsync(contentId, cancellationToken);
        }
        catch (PlaybackUnavailableException ex)
        {
            Fail(ex.Message, ex.CanRetry);
            return;
        }
        (Title, Subtitle) = PlaybackStarter.Titles(_context.Watch);
        SetStatus(Status);
        await StartAttemptAsync(PlaybackStarter.StartPosition(_context.Watch, request.FromBeginning), cancellationToken);
    }

    public void Seek(double sourceSeconds)
    {
        if (_plan is not { } plan || _stopping || Status.Phase != ServerPlaybackPhase.Playing)
        {
            return;
        }
        var timeline = plan.Timeline;
        if (timeline.CanSeekAnywhere || IsInsideStream(timeline, sourceSeconds))
        {
            _engine.Seek(TimeSpan.FromSeconds(Math.Max(0, timeline.ToPlayer(sourceSeconds))));
            return;
        }
        _ = Guarded(() => ReanchorAsync(sourceSeconds));
    }

    public Task StopAsync()
    {
        if (_stopping)
        {
            return _stop;
        }
        _stopping = true;
        _generation++;
        StopProgressTimer();
        // Read now: the engine stops right after this call, and its idle state would read as position 0.
        double position = SourcePosition;
        bool paused = _paused;
        _stop = StopSessionAsync(position, paused);
        return _stop;
    }

    public async ValueTask DisposeAsync()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Ended -= OnEngineEnded;
        StopProgressTimer();
        await _stop.ConfigureAwait(false);
    }

    // A new attempt: a fresh attempt id and, from the server, a fresh session.
    private async Task StartAttemptAsync(double? startPosition, CancellationToken cancellationToken = default)
    {
        var context = _context!;
        for (int tries = 0; ; tries++)
        {
            _attemptId = Guid.NewGuid().ToString();
            PlaybackDecision decision;
            try
            {
                decision = await _starter.StartAsync(context, _attemptId, startPosition, cancellationToken);
            }
            catch (SiloApiException ex) when (ex is { Status: 409, Code: ProblemCodes.InstallationChanged } && tries == 0)
            {
                // The server was reinstalled: ask for its new installation once, then start anew.
                _logger.LogInformation("The server's playback installation changed; starting again");
                try
                {
                    context = _context = await _starter.WithFreshCapabilitiesAsync(context, cancellationToken);
                }
                catch (PlaybackUnavailableException unavailable)
                {
                    Fail(unavailable.Message, unavailable.CanRetry);
                    return;
                }
                continue;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
            {
                _logger.LogWarning("Starting playback failed: {Failure}", ApiFailureText.Describe(ex));
                var described = PlaybackStarter.Describe(ex);
                Fail(described.Message, described.CanRetry);
                return;
            }
            ResetPlanChain();
            await AdoptAsync(decision, newSession: true, restorePosition: null);
            return;
        }
    }

    // Takes a decision: a plan is loaded, anything else explains itself.
    private async Task AdoptAsync(PlaybackDecision decision, bool newSession, double? restorePosition)
    {
        if (_stopping)
        {
            return;
        }
        if (!decision.IsCompatible)
        {
            Fail("The server answered with a playback protocol this app does not support.", canRetry: false);
            return;
        }
        if (!decision.IsPlayable)
        {
            SendRouteEvent(RouteEvents.Terminal, withPlan: !newSession, failure: decision.Terminal?.Reason);
            string message = decision.Terminal?.Message is { Length: > 0 } text ? text : "This title cannot be played right now.";
            Fail(message, decision.Terminal?.Retryable ?? false);
            return;
        }
        var plan = decision.PlaybackPlan!;
        if (newSession || _progress?.SessionId != decision.SessionId)
        {
            _progress = new PlaybackProgress(_api, _context!.Server.BaseUri, decision.SessionId!, _context.InstallationId,
                _loggers.CreateLogger<PlaybackProgress>(), _options.RetryPolicy);
        }
        _plan = plan;
        _planAttemptId = Guid.NewGuid().ToString();
        _firstFrameReported = false;
        if (plan.RequiresClientExecution)
        {
            // The plan wants work done on this side that this client does not do (8.12, item 9).
            await RecoverFromFailureAsync("client_execution_unsupported", "The plan needs client-side processing.");
            return;
        }
        if (PlaybackUrl.Resolve(_context!.Server.BaseUri, plan.Stream.Url) is not { } url)
        {
            Fail("The server sent a stream address this app does not trust.", canRetry: true);
            return;
        }
        _streamUrl = url;
        SendRouteEvent(RouteEvents.PlanSelected);
        var timeline = plan.Timeline;
        double start = restorePosition is { } position && timeline.SeekRestoration == SeekRestorations.PlayerPosition
            ? timeline.ToPlayer(position)
            : timeline.PlayerStartSeconds;
        await LoadAsync(start, rejected: false);
    }

    private async Task LoadAsync(double playerStart, bool rejected)
    {
        MediaCredentials media;
        try
        {
            media = await _auth.GetAsync(_context!.Server, rejected, CancellationToken.None);
        }
        catch (MediaAuthorizationException ex)
        {
            Fail(ex.Message, ex.Failure == MediaAuthorizationFailure.Unavailable);
            return;
        }
        if (_stopping)
        {
            return;
        }
        // The plan's own headers are empty today; they would go to the same server only.
        var headers = media.Headers.Concat(_plan!.Stream.Headers.Where(h => !media.Headers.Any(m => m.Key.Equals(h.Key, StringComparison.OrdinalIgnoreCase)))).ToList();
        _generation++;
        _started = false;
        _loadedAt = _time.GetTimestamp();
        _logger.LogInformation("Loading the {Delivery} plan at {Start:F1} s (offset {Offset:F1} s)",
            _plan.Delivery, playerStart, _plan.Timeline.TimelineOffsetSeconds);
        _engine.Load(new MediaSource(_streamUrl!, headers, TimeSpan.FromSeconds(Math.Max(0, playerStart))));
    }

    private void OnEngineStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() => ShowEngineState(state));

    private void ShowEngineState(PlaybackSnapshot state)
    {
        if (_stopping || _plan is null)
        {
            return;
        }
        // An idle engine (between loads) reads as position 0; only a playing stream says where the viewer is.
        if (state is { IsLoaded: true, HasStarted: true })
        {
            _playerPosition = state.Position.TotalSeconds;
        }
        bool pauseChanged = _started && state.IsPaused != _paused;
        _paused = state.IsPaused;
        if (state is { IsLoaded: true, HasStarted: true } && !_started)
        {
            _started = true;
            OnStreamStarted(state);
        }
        if (pauseChanged)
        {
            // The server learns about a pause only from a sample, and a paused session lives 30 minutes, not 45 s.
            _ = Guarded(ReportProgressAsync);
        }
    }

    private void OnStreamStarted(PlaybackSnapshot state)
    {
        _authReloads = 0;
        _reconnecting = false;
        if (!_firstFrameReported)
        {
            _firstFrameReported = true;
            long milliseconds = (long)_time.GetElapsedTime(_loadedAt).TotalMilliseconds;
            var diagnostics = new Dictionary<string, string> { [RouteDiagnostics.FirstFrameMs] = milliseconds.ToString(CultureInfo.InvariantCulture) };
            if (state.HardwareDecoder is { Length: > 0 } decoder)
            {
                diagnostics[RouteDiagnostics.DecoderName] = decoder;
            }
            SendRouteEvent(RouteEvents.FirstFrame, diagnostics: diagnostics);
        }
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _progressTimer ??= _time.CreateTimer(_ => _dispatcher.Post(() => _ = Guarded(ReportProgressAsync)), null, ProgressInterval, ProgressInterval);
    }

    private void OnEngineEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
        if (_stopping || _plan is null || ended.Reason == PlaybackEndReason.Stopped)
        {
            return;
        }
        if (ended.Reason == PlaybackEndReason.AuthenticationFailed)
        {
            _ = Guarded(ReloadAfterRejectionAsync);
        }
        else if (ended.Reason == PlaybackEndReason.EndOfFile && IsAtTheEnd())
        {
            _ = Guarded(FinishAsync);
        }
        else if (_started || Status.Phase == ServerPlaybackPhase.Reconnecting)
        {
            _ = Guarded(ReconnectAsync);
        }
        else
        {
            _ = Guarded(() => RecoverFromFailureAsync("load_failed", ended.Detail));
        }
    });

    private bool IsAtTheEnd() => DurationSeconds is not { } duration || SourcePosition >= duration - EndTolerance;

    // The title played to its end: the stop carries the final position, which lets the server mark it watched.
    private async Task FinishAsync()
    {
        await StopAsync();
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
    }

    private async Task ReportProgressAsync()
    {
        if (_progress is not { } progress || _stopping || _reconnecting || !_started)
        {
            return;
        }
        int generation = _generation;
        var outcome = await progress.ReportAsync(SourcePosition, _paused);
        if (_stopping || generation != _generation)
        {
            return;
        }
        switch (outcome)
        {
            case ProgressOutcome.SessionGone:
                // Expired or forgotten (a server restart): the viewer is still here, so start again where they are.
                await RestartAsync();
                break;
            case ProgressOutcome.InstallationChanged:
                _context = await _starter.WithFreshCapabilitiesAsync(_context!, CancellationToken.None);
                await RestartAsync();
                break;
            case ProgressOutcome.SessionEnded:
                EndedByServer();
                break;
        }
    }

    private async Task StopSessionAsync(double position, bool paused)
    {
        if (_progress is not { } progress)
        {
            return;
        }
        await progress.StopAsync(position, paused);
        SendRouteEvent(RouteEvents.Stopped);
    }

    private void SendRouteEvent(string name, bool withPlan = true, string? failure = null, IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        if (_context is not { } context)
        {
            return;
        }
        var routeEvent = new PlaybackRouteEvent
        {
            InstallationId = context.InstallationId,
            EventId = Guid.NewGuid().ToString(),
            PlaybackAttemptId = _attemptId,
            SessionId = withPlan ? _progress?.SessionId : null,
            PlanId = withPlan ? _plan?.PlanId : null,
            PlanAttemptId = withPlan && _plan is not null ? _planAttemptId : null,
            PlanAttemptKey = withPlan ? _plan?.PlanAttemptKey : null,
            Event = name,
            FailureClassification = failure is { Length: > 64 } ? failure[..64] : failure,
            Diagnostics = diagnostics ?? new Dictionary<string, string>(),
        };
        _ = SendAsync();

        // Diagnostics only: whatever happens to them, playback goes on.
        async Task SendAsync()
        {
            try
            {
                await _api.SendRouteEventAsync(context.Server.BaseUri, routeEvent);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogDebug("Route event {Event} was dropped: {Failure}", name, ApiFailureText.Describe(ex));
            }
        }
    }

    private void Fail(string message, bool canRetry)
    {
        StopProgressTimer();
        _generation++;
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, message, canRetry));
    }

    private void SetStatus(ServerPlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Dispose();
        _progressTimer = null;
    }

    // Work started from an event or a timer has no caller to report to: log what it did not expect.
    private async Task Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server playback failed unexpectedly");
            if (!_stopping)
            {
                Fail("Playback stopped because of an unexpected problem.", canRetry: true);
            }
        }
    }
}
