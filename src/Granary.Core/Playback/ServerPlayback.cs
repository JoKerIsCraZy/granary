using System.Globalization;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
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
/// Used on the UI thread: engine events and timers are posted there, so no state is shared across threads. One
/// recovery runs at a time (<see cref="Recover"/>). Answers that arrive after the playback moved on (a stop, a
/// failure, another load) are dropped by <see cref="_generation"/>, engine events of an earlier load by their load
/// id. After the stop, the playback leaves the engine alone: it may already play the next title.
/// </remarks>
public sealed partial class ServerPlayback : IServerPlayback
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    // mpv pauses a moment before it reports that a stream broke off: a pause is reported once it has held this long.
    private static readonly TimeSpan PauseSettle = TimeSpan.FromSeconds(1);

    // The server may hold a manifest for 30 s and a segment for 12 s more, longer after a seek (docs/api-contract.md,
    // 9.5): a load that shows no picture in this time has failed.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(90);

    // mpv reports a stream that broke off as its end: an end earlier than this before the title's end is a lost
    // connection, not the end of the title.
    private const double EndTolerance = 30;

    private const string AutoPlayNextKey = "playback.auto_play_next";

    private readonly PlaybackStarter _starter;
    private readonly PlaybackApi _api;
    private readonly TrackPreferencesApi _preferences;
    private readonly SettingsApi _settings;
    private readonly MarkersApi _markers;
    private readonly IMediaAuthorization _auth;
    private readonly IMediaProxy _proxy;
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly ServerPlaybackOptions _options;
    private readonly PlaybackControlLane? _controlLane;

    private PlaybackContext? _context;
    private string _attemptId = "";
    private PlaybackPlan? _plan;

    // The tracks that play: the plan's, or a pick switched in the player since. Replans carry them on.
    private SelectedTracks _tracks = new();
    private string _planAttemptId = "";
    private PlaybackProgress? _progress;
    private Uri? _streamUrl;

    // The attempt agreed on proxy-node media: the client offered authorized_media_origins_v1 and the start answer
    // confirmed it (Apple's client checks the same).
    private bool _mediaOrigins;
    private IMediaRoute? _route;
    private ITimer? _progressTimer;
    private ITimer? _pauseReport;
    private ITimer? _firstFrameWatch;

    // The engine load that is this playback's; 0 once it ended.
    private long _loadId;
    private double _playerPosition;
    private bool _paused;

    // The load showed its first frame.
    private bool _started;

    // The title was handed to the engine once: a later load (a replan, a reconnect) keeps whether it is paused.
    private bool _titleLoaded;

    // The plan showed a first frame in one of its loads: a later failure is a lost connection, not a failed route.
    private bool _planPlayed;

    // The session showed a first frame; before that it leaves the viewer nowhere.
    private bool _sessionPlayed;
    private bool _firstFrameReported;
    private long _loadedAt;
    private bool _stopping;
    private Task _stop = Task.CompletedTask;
    private Task _starting = Task.CompletedTask;
    private Task _retired = Task.CompletedTask;
    private int _generation;

    public ServerPlayback(
        PlaybackStarter starter,
        PlaybackApi api,
        TrackPreferencesApi preferences,
        SettingsApi settings,
        MarkersApi markers,
        IMediaAuthorization auth,
        IMediaProxy proxy,
        IPlaybackEngine engine,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILoggerFactory loggers,
        ServerPlaybackOptions? options = null,
        Func<PlaybackControlLane>? controlLanes = null)
    {
        _starter = starter;
        _api = api;
        _preferences = preferences;
        _settings = settings;
        _markers = markers;
        _auth = auth;
        _proxy = proxy;
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ServerPlayback>();
        _options = options ?? ServerPlaybackOptions.Default;
        _controlLane = controlLanes?.Invoke();
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

    public PlayingEpisode? Episode => _context?.Watch is { Type: WatchItemTypes.Episode, SeriesId: { Length: > 0 } series,
        SeasonNumber: { } season, EpisodeNumber: { } episode } watch
        ? new PlayingEpisode(_context.ContentId.Length > 0 ? _context.ContentId : watch.ContentId, series, watch.SeriesTitle ?? "", season, episode)
        : null;

    public void SetAutoPlayNext(bool enabled)
    {
        if (_context is not { } context || context.Preferences.Markers.AutoPlayNext == enabled)
        {
            return;
        }
        var preferences = context.Preferences;
        _context = context with { Preferences = preferences with { Markers = preferences.Markers with { AutoPlayNext = enabled } } };
        var value = JsonDocument.Parse(enabled ? "true" : "false").RootElement.Clone();
        SaveChoice("auto-play", cancellationToken =>
            _settings.SetValueAsync(context.Server.BaseUri, AutoPlayNextKey, value, SettingScopes.Profile, cancellationToken));
    }

    // Where the viewer is in the title.
    private double SourcePosition => _plan?.Timeline.ToSource(_playerPosition) ?? _playerPosition;

    // Where a stop leaves the viewer: nowhere before the session's first frame (0, which the server never stores), so
    // opening a title and leaving at once does not move its resume point back by the rewind.
    private double StopPosition => _sessionPlayed ? SourcePosition : 0;

    public async Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentId is not { } contentId)
        {
            throw new ArgumentException("A server playback plays a server title.", nameof(request));
        }
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        _starting = Guarded(() => PrepareAndStartAsync(contentId, request.FromBeginning, cancellationToken));
        await _starting;
    }

    public void Seek(double sourceSeconds)
    {
        if (_plan is not { } plan || _stopping || Status.Phase != ServerPlaybackPhase.Playing)
        {
            return;
        }
        if (_recovering || !_started)
        {
            // The stream is about to be replaced or has not started: the seek waits for the stream that plays.
            _pendingSeek = sourceSeconds;
            return;
        }
        var timeline = plan.Timeline;
        if (timeline.CanSeekAnywhere || IsInsideStream(timeline, sourceSeconds))
        {
            // The target is where the viewer is from now on, also for a reload before the engine gets there.
            _playerPosition = Math.Max(0, timeline.ToPlayer(sourceSeconds));
            _engine.Seek(TimeSpan.FromSeconds(_playerPosition));
            return;
        }
        Recover(() => ReanchorAsync(sourceSeconds));
    }

    public Task StopAsync()
    {
        if (_stopping)
        {
            return _stop;
        }
        _stopping = true;
        _generation++;
        // A delay or a speed the viewer just set is saved now, not when its wait would have ended.
        SaveSubtitleDelayNow();
        SaveSpeedNow();
        StopTimers();
        StopTrickplay();
        StopControlLane();
        // Read now: the engine stops right after this call, and its idle state would read as position 0. Closing the
        // app waits for the stop, and for a pick still being saved.
        _stop = Task.WhenAll(StopSessionsAsync(StopPosition, _paused), _trackSaves);
        CloseRoute();
        return _stop;
    }

    public async ValueTask DisposeAsync()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Ended -= OnEngineEnded;
        StopTimers();
        StopTrickplay();
        // Saves still waiting go with the stop; without one there is nobody left to save for.
        _speedSave?.Dispose();
        _subtitleDelaySave?.Dispose();
        CloseRoute();
        if (_controlLane is { } lane)
        {
            await lane.DisposeAsync().ConfigureAwait(false);
        }
        await _stop.ConfigureAwait(false);
        await _retired.ConfigureAwait(false);
        await _trackSaves.ConfigureAwait(false);
    }

    private async Task PrepareAndStartAsync(string contentId, bool fromBeginning, CancellationToken cancellationToken)
    {
        try
        {
            _context = await _starter.PrepareAsync(contentId, cancellationToken);
        }
        catch (PlaybackUnavailableException ex)
        {
            Fail(ex.Message, ex.CanRetry);
            return;
        }
        if (_stopping)
        {
            return;
        }
        (Title, Subtitle) = PlaybackStarter.Titles(_context.Watch);
        _engine.SetSubtitleLook(_context.Preferences.Subtitles);
        _subtitleDelayMs = _context.Preferences.SubtitleDelayMs;
        _speed = _context.Preferences.Speed;
        _engine.SetSpeed(_speed);
        SetStatus(Status);
        await StartAttemptAsync(PlaybackStarter.StartPosition(_context.Watch, fromBeginning), cancellationToken);
    }

    // A new attempt: a fresh attempt id and, from the server, a fresh session.
    private async Task StartAttemptAsync(double? startPosition, CancellationToken cancellationToken = default)
    {
        for (int tries = 0; ; tries++)
        {
            _attemptId = Guid.NewGuid().ToString();
            PlaybackDecision decision;
            try
            {
                decision = await _starter.StartAsync(_context!, _attemptId, startPosition, cancellationToken);
            }
            catch (SiloApiException ex) when (ex is { Status: 409, Code: ProblemCodes.InstallationChanged } && tries == 0)
            {
                // The server was reinstalled: ask for its new installation once, then start anew.
                _logger.LogInformation("The server's playback installation changed; starting again");
                if (await RefreshCapabilitiesAsync(cancellationToken))
                {
                    continue;
                }
                return;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
            {
                _logger.LogWarning("Starting playback failed: {Failure}", ApiFailureText.Describe(ex));
                var described = PlaybackStarter.Describe(ex);
                Fail(described.Message, described.CanRetry);
                return;
            }
            if (_stopping)
            {
                // Left while the server was starting: end the session it made for nobody.
                if (decision.SessionId is { } session)
                {
                    Retire(NewSession(session), position: 0);
                }
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
        if (newSession)
        {
            _mediaOrigins = _context!.Report.ClientFeatures.Contains(PlaybackFeatures.AuthorizedMediaOriginsV1)
                && PlaybackProtocol.HasFeature(decision.ServerFeatures, PlaybackFeatures.AuthorizedMediaOriginsV1);
        }
        if (newSession || _progress?.SessionId != decision.SessionId)
        {
            if (_progress is { } previous)
            {
                // The new attempt replaces that session: it ends where the viewer was, now rather than when it idles out.
                Retire(previous, StopPosition);
            }
            _progress = NewSession(decision.SessionId!);
            _sessionPlayed = false;
            KeepControlLane();
        }
        if (_plan?.PlanAttemptKey != plan.PlanAttemptKey)
        {
            _planPlayed = false;
        }
        _plan = plan;
        TakeTracks(plan);
        TakeAddedSubtitle(plan);
        _planAttemptId = Guid.NewGuid().ToString();
        _firstFrameReported = false;
        if (plan.RequiresClientExecution)
        {
            // The plan wants work done on this side that this client does not do (8.12, item 9).
            await RecoverFromFailureAsync("client_execution_unsupported", "The plan needs client-side processing.");
            return;
        }
        var server = _context!.Server.BaseUri;
        if (PlaybackUrl.ResolveStream(server, plan.Stream.Url, _progress!.SessionId, _mediaOrigins) is not { } url)
        {
            if (PlaybackUrl.IsElsewhere(server, plan.Stream.Url))
            {
                // A proxy node this client does not take: the server replays the plan through its own address (8.7).
                await RecoverFromFailureAsync(FailureClassifications.SourceRefused, "The stream's origin was not accepted.");
                return;
            }
            Fail("The server sent a stream address this app does not trust.", canRetry: true);
            return;
        }
        if (plan.Stream.Protocol == StreamProtocols.Hls && !IsSessionPlaylist(url, _progress.SessionId))
        {
            // Its route reaches everything beside the playlist with the viewer's headers: that must be this session's
            // own folder (…/{session}/master.m3u8, 9.2), never a shallow one where other routes of the server lie.
            Fail("The server sent a stream address this app does not trust.", canRetry: true);
            return;
        }
        _streamUrl = url;
        if (AudioStream(plan, _tracks) is { } stream && !FileHasAudioStream(stream))
        {
            // client_selected_audio_track_v1 promises the ordinal maps onto the file; a stream it lacks would play
            // silent, so the server is asked for a route that packages the track instead (8.8).
            await RecoverFromFailureAsync("audio_track_unavailable", $"The file has no audio stream {stream}.");
            return;
        }
        SendRouteEvent(RouteEvents.PlanSelected);
        var timeline = plan.Timeline;
        double start = restorePosition is { } position && timeline.SeekRestoration == SeekRestorations.PlayerPosition
            ? timeline.ToPlayer(position)
            : timeline.PlayerStartSeconds;
        await LoadAsync(start, rejected: false);
    }

    private async Task LoadAsync(double playerStart, bool rejected)
    {
        int generation = _generation;
        MediaCredentials media;
        try
        {
            media = await _auth.GetAsync(_context!.Server, rejected, CancellationToken.None);
        }
        catch (MediaAuthorizationException ex)
        {
            if (!Superseded(generation))
            {
                Fail(ex.Message, ex.Failure == MediaAuthorizationFailure.Unavailable);
            }
            return;
        }
        if (Superseded(generation))
        {
            return;
        }
        // Only the media headers this client knows go out. The plan's own headers are empty today, and a server
        // that sent some could otherwise set anything (Cookie, Host, Range) on every request of the load.
        if (_plan!.Stream.Headers.Count > 0)
        {
            _logger.LogWarning("The plan asks for {Count} header(s) of its own; they are not sent", _plan.Stream.Headers.Count);
        }
        _generation++;
        _started = false;
        _endedDuringRecovery = null;
        // Until its first frame the viewer is at the load's start, in this plan's time.
        _playerPosition = Math.Max(0, playerStart);
        _loadedAt = _time.GetTimestamp();
        int? audio = AudioStream(_plan, _tracks);
        _logger.LogInformation("Loading the {Delivery} plan at {Start:F1} s (offset {Offset:F1} s, audio stream {Audio})",
            _plan.Delivery, _playerPosition, _plan.Timeline.TimelineOffsetSeconds, audio?.ToString(CultureInfo.InvariantCulture) ?? "as delivered");
        // mpv gets the loopback and no credential: the proxy adds the headers to each request of this stream alone,
        // renewing a token the server refuses. A playlist's variants and segments lie beside it.
        var server = _context!.Server;
        CloseRoute();
        _route = _proxy.Open(_streamUrl!, _plan.Stream.Protocol == StreamProtocols.Hls ? MediaRouteScope.Directory : MediaRouteScope.File,
            (refused, cancellationToken) => _auth.GetAsync(server, refused, cancellationToken), media);
        // Subtitles are the plan's to choose; left to itself, mpv would show one the file marks as default.
        _loadId = _engine.Load(new MediaSource(_route.LocalUrl, [], TimeSpan.FromSeconds(_playerPosition), audio, SubtitleStream: 0));
        if (!_titleLoaded)
        {
            // The title plays, however the one before was left: mpv keeps a pause across files, and pauses itself
            // at a file's end (keep-open), which is where the next episode starts from.
            _titleLoaded = true;
            _engine.SetPaused(false);
        }
        WatchFirstFrame(_loadId);
    }

    // Ends the requests of the stream before, its subtitles' too; a new load has its own routes.
    private void CloseRoute()
    {
        _route?.Dispose();
        _route = null;
        CloseSubtitleRoutes();
    }

    // A file the watch detail does not list, or lists without tracks, cannot be checked.
    private bool FileHasAudioStream(int stream) =>
        PlayingVersion is not { } version || version.AudioTracks.Count == 0 || stream <= version.AudioTracks.Count;

    // A direct play is the file itself, with every audio track, and the player would take the one the file marks as
    // default: it must play the one selected (8.8). A stream the server made carries only that one. The ordinal is the
    // track's place among the file's audio streams (file:{id}:audio:{ordinal}); mpv counts them from 1.
    private static int? AudioStream(PlaybackPlan plan, SelectedTracks tracks) =>
        plan.Delivery == PlaybackDeliveries.OriginalHttp && tracks.Audio is { } audio && Ordinal(audio) is { } ordinal
            ? ordinal + 1
            : null;

    private void OnEngineStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() => ShowEngineState(state));

    private void ShowEngineState(PlaybackSnapshot state)
    {
        if (_stopping || _loadId == 0 || state.LoadId != _loadId)
        {
            return;
        }
        // A load held at the end of a stream that broke off reads as the end of the data, paused: only a stream that
        // plays on says where the viewer is.
        if (state.IsAtEnd)
        {
            return;
        }
        bool playing = state is { IsLoaded: true, HasStarted: true };
        if (playing)
        {
            _playerPosition = state.Position.TotalSeconds;
        }
        bool pauseChanged = _started && state.IsPaused != _paused;
        _paused = state.IsPaused;
        if (playing && !_started)
        {
            _started = true;
            OnStreamStarted(state);
        }
        if (pauseChanged)
        {
            // The server learns about a pause only from a sample, and a paused session lives 30 minutes, not 45 s.
            SchedulePauseReport();
        }
    }

    private void OnStreamStarted(PlaybackSnapshot state)
    {
        StopFirstFrameWatch();
        _authReloads = 0;
        _reconnectTries = 0;
        _planPlayed = true;
        _sessionPlayed = true;
        ReadMarkers();
        ReadTrickplay();
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
        if (_pendingSeek is { } target)
        {
            _pendingSeek = null;
            Seek(target);
        }
        _ = ShowSubtitlesAsync(_loadId);
        ApplyPendingTracks();
    }

    private void OnEngineEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
        if (_stopping || _loadId == 0 || ended.LoadId != _loadId || ended.Reason == PlaybackEndReason.Stopped)
        {
            return;
        }
        OnLoadEnded(ended);
    });

    // The load is over: what comes next depends on how it ended and whether its plan ever played.
    private void OnLoadEnded(PlaybackEnded ended)
    {
        bool played = _started;
        _loadId = 0;
        _started = false;
        StopFirstFrameWatch();
        if (_recovering)
        {
            _endedDuringRecovery = (ended, played);
            return;
        }
        TakeLoadEnd(ended, played);
    }

    private void TakeLoadEnd(PlaybackEnded ended, bool played)
    {
        if (ended.Reason == PlaybackEndReason.AuthenticationFailed)
        {
            Recover(ReloadAfterRejectionAsync);
        }
        else if (ended.Reason == PlaybackEndReason.EndOfFile && IsAtTheEnd())
        {
            Recover(FinishAsync);
        }
        else if (played || _planPlayed || _reconnectTries > 0)
        {
            Recover(ReconnectAsync);
        }
        else
        {
            // A proxy node that never delivered may be out of reach from here; the server can take the plan home.
            string classification = IsFromNode(_streamUrl) ? FailureClassifications.SourceRefused : "load_failed";
            Recover(() => RecoverFromFailureAsync(classification, ended.Detail));
        }
    }

    private static bool IsSessionPlaylist(Uri playlist, string sessionId) =>
        playlist.AbsolutePath.EndsWith("/" + Uri.EscapeDataString(sessionId) + "/master.m3u8", StringComparison.Ordinal);

    private bool IsFromNode(Uri? stream) => stream is not null && _context is { } context && !SiloRequestOptions.IsBeneath(stream, context.Server.BaseUri);

    // Without a known length an end can only be taken for the end: neither the plan nor the file says otherwise.
    private bool IsAtTheEnd() => DurationSeconds is not { } duration || SourcePosition >= duration - EndTolerance;

    // The title played to its end: the stop carries the final position, which lets the server mark it watched.
    private Task FinishAsync()
    {
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        return StopAsync();
    }

    private void WatchFirstFrame(long loadId)
    {
        _firstFrameWatch?.Dispose();
        _firstFrameWatch = _time.CreateTimer(_ => _dispatcher.Post(() => OnFirstFrameOverdue(loadId)), null, FirstFrameTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopFirstFrameWatch()
    {
        _firstFrameWatch?.Dispose();
        _firstFrameWatch = null;
    }

    private void OnFirstFrameOverdue(long loadId)
    {
        if (_stopping || loadId != _loadId || _started)
        {
            return;
        }
        _logger.LogWarning("The stream showed no picture within {Seconds:F0} s", FirstFrameTimeout.TotalSeconds);
        // Whatever the load still waits for must not start playing behind the next one.
        _engine.Stop();
        OnLoadEnded(new PlaybackEnded(PlaybackEndReason.Error, "No picture within the start timeout.", loadId));
    }

    private async Task ReportProgressAsync()
    {
        if (_progress is not { } progress || _stopping || !_started)
        {
            return;
        }
        int generation = _generation;
        var outcome = await progress.ReportAsync(SourcePosition, _paused);
        if (Superseded(generation))
        {
            return;
        }
        switch (outcome)
        {
            case ProgressOutcome.SessionGone:
                // Expired or forgotten (a server restart): the viewer is still here, so start again where they are.
                Recover(() => RestartAsync());
                break;
            case ProgressOutcome.InstallationChanged:
                Recover(async () =>
                {
                    if (await RefreshCapabilitiesAsync())
                    {
                        await RestartAsync();
                    }
                });
                break;
            case ProgressOutcome.SessionEnded:
                EndedByServer();
                break;
        }
    }

    private async Task StopSessionsAsync(double position, bool paused)
    {
        // A start still on its way makes a session too; it ends that one itself once the server answers.
        await _starting;
        await _retired;
        if (_progress is not { } progress)
        {
            return;
        }
        await progress.StopAsync(position, paused);
        SendRouteEvent(RouteEvents.Stopped);
    }

    private PlaybackProgress NewSession(string sessionId) =>
        new(_api, _context!.Server.BaseUri, sessionId, _context.InstallationId, _loggers.CreateLogger<PlaybackProgress>(), _options.RetryPolicy);

    // Ends a session this playback no longer plays; the stop and the dispose wait for it.
    private void Retire(PlaybackProgress session, double position)
    {
        var stop = StopRetiredAsync(session, position, _paused);
        _retired = _retired.IsCompleted ? stop : Task.WhenAll(_retired, stop);
    }

    private async Task StopRetiredAsync(PlaybackProgress session, double position, bool paused)
    {
        try
        {
            await session.StopAsync(position, paused);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping the replaced session {SessionId} failed", session.SessionId);
        }
    }

    // After 409 installation_changed: the server was reinstalled, so its installation id is new.
    private async Task<bool> RefreshCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _context = await _starter.WithFreshCapabilitiesAsync(_context!, cancellationToken);
            return !_stopping;
        }
        catch (PlaybackUnavailableException ex)
        {
            Fail(ex.Message, ex.CanRetry);
            return false;
        }
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
        if (_stopping)
        {
            return;
        }
        StopTimers();
        StopControlLane();
        _generation++;
        _loadId = 0;
        _started = false;
        _engine.Stop();
        CloseRoute();
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, message, canRetry));
    }

    private void SetStatus(ServerPlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void SchedulePauseReport()
    {
        _pauseReport?.Dispose();
        int generation = _generation;
        bool paused = _paused;
        _pauseReport = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (!Superseded(generation) && paused == _paused)
            {
                _ = Guarded(ReportProgressAsync);
            }
        }), null, PauseSettle, Timeout.InfiniteTimeSpan);
    }

    private void StopTimers()
    {
        _progressTimer?.Dispose();
        _progressTimer = null;
        _pauseReport?.Dispose();
        _pauseReport = null;
        StopFirstFrameWatch();
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
            Fail("Playback stopped because of an unexpected problem.", canRetry: true);
        }
    }
}
