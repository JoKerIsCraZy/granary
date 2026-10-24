using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

/// <summary>
/// Device (QR code) sign-in on one server (docs/api-contract.md, 4.2), after Android's
/// <c>DeviceSignInMachine</c>: gets a code, polls it, replaces it in place when the server drops it or
/// the local deadline passes, backs off on network errors, 5xx and 429, and hands the tokens of an
/// approval to <see cref="ISessionManager"/>.
/// </summary>
/// <remarks>
/// <see cref="Stop"/> (or a new <see cref="StartAsync"/>, which supersedes the running sign-in) cancels
/// the wait or call in progress and drops any answer still arriving, an approval included: once stopped,
/// the flow does not sign in. A code the server may still hold is withdrawn when a run ends.
/// </remarks>
public sealed class DeviceSignInFlow : IAsyncDisposable, IDisposable
{
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StartUnreachableAfter = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan PollUnreachableAfter = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A code replaced sooner than this points at a misbehaving server: new codes are paced.</summary>
    internal static readonly TimeSpan MinimumCodeLifetime = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(1);
    private const string CapabilityDisabled = "capability_disabled";
    private const string CapabilityNotConfigured = "capability_not_configured";

    private readonly DeviceLoginApi _api;
    private readonly ISessionManager _session;
    private readonly ClientIdentity _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceSignInFlow> _logger;
    private readonly Lock _gate = new();
    private Run? _active;
    private long _generation;
    private bool _disposed;
    private DeviceSignInState _current = DeviceSignInState.Idle;

    public DeviceSignInFlow(DeviceLoginApi api, ISessionManager session, ClientIdentity identity, TimeProvider time, ILogger<DeviceSignInFlow> logger)
    {
        _api = api;
        _session = session;
        _identity = identity;
        _time = time;
        _logger = logger;
    }

    /// <summary>The latest state of the latest run.</summary>
    public DeviceSignInState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Raised on background threads. A superseded run's last event can arrive after the new run's
    /// first; read <see cref="Current"/> for the latest state.
    /// </summary>
    public event EventHandler<DeviceSignInState>? StateChanged;

    /// <summary>
    /// Runs device sign-in on <paramref name="server"/> until it is approved, denied, unavailable or
    /// failed, or until <see cref="Stop"/>; all of these complete normally. Throws only when
    /// <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    public Task StartAsync(ServerEntry server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        Run run;
        Run? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _active;
            run = new Run(this, server, ++_generation, cancellationToken);
            _active = run;
            _current = new DeviceSignInState(DeviceSignInStatus.GettingCode);
        }
        previous?.RequestStop();
        return Task.Run(run.ExecuteAsync, CancellationToken.None);
    }

    /// <summary>Ends the running sign-in, if any, and withdraws its code on the server.</summary>
    public void Stop()
    {
        Run? run;
        lock (_gate)
        {
            run = _active;
        }
        run?.RequestStop();
    }

    /// <summary>Stops the running sign-in and waits until its code is withdrawn.</summary>
    public async ValueTask DisposeAsync()
    {
        if (StopForGood() is { } run)
        {
            await run.Completion.ConfigureAwait(false);
        }
    }

    public void Dispose() => StopForGood();

    private Run? StopForGood()
    {
        Run? run;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
            _disposed = true;
            run = _active;
        }
        run?.RequestStop();
        return run;
    }

    private void Publish(Run run, DeviceSignInState state)
    {
        lock (_gate)
        {
            // A run's first state is always raised: StartAsync already set it as the current one.
            if (run.Generation != _generation || (run.HasPublished && _current == state))
            {
                return;
            }
            run.HasPublished = true;
            _current = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private void End(Run run)
    {
        bool stopped;
        lock (_gate)
        {
            if (_active == run)
            {
                _active = null;
            }
            stopped = !_current.IsFinal;
        }
        if (stopped)
        {
            Publish(run, DeviceSignInState.Idle);
        }
    }

    private static TimeSpan NextBackoff(TimeSpan current, TimeSpan interval) =>
        current <= TimeSpan.Zero
            ? Clamp(interval, MinimumWait, MaxBackoff)
            : Clamp(current * 2, MinimumWait, MaxBackoff);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private enum Failure
    {
        /// <summary>Network error, timeout, unreadable answer, 408 or 5xx: the request may still be live.</summary>
        Transient,

        /// <summary>429.</summary>
        RateLimited,

        /// <summary>404, 410, 501 or a disabled capability: no such request (poll), no device sign-in (start).</summary>
        Gone,

        /// <summary>Any other 4xx: retrying the same call will not help.</summary>
        Rejected,
    }

    /// <summary>One <see cref="StartAsync"/> call. Its steps run one after the other, never concurrently.</summary>
    private sealed class Run
    {
        private readonly DeviceSignInFlow _flow;
        private readonly ServerEntry _server;
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _stop;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _epoch;
        private DeviceLoginCapability? _capability;
        private LiveCode? _live;
        private TimeSpan? _failingSince;
        private TimeSpan _startBackoff;
        private TimeSpan _renewBackoff;
        private TimeSpan _startDelay;

        public Run(DeviceSignInFlow flow, ServerEntry server, long generation, CancellationToken caller)
        {
            _flow = flow;
            _server = server;
            _caller = caller;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _epoch = flow._time.GetTimestamp();
            Generation = generation;
        }

        public long Generation { get; }

        /// <summary>Read and written under the flow's lock.</summary>
        public bool HasPublished { get; set; }

        /// <summary>Completes when the run has ended and withdrawn its code; never faults.</summary>
        public Task Completion => _completion.Task;

        private TimeSpan Now => _flow._time.GetElapsedTime(_epoch);

        private string Origin => _server.BaseUri.GetLeftPart(UriPartial.Authority);

        private ILogger Logger => _flow._logger;

        // Unknown support (the capability read failed) still gets the cancel: a server without it answers 404.
        private bool MayWithdraw => _capability?.Cancel != false;

        public void RequestStop()
        {
            try
            {
                // Asynchronous, so a caller on the UI thread does not run the rest of the run inline.
                _ = _stop.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The run has already ended.
            }
        }

        public async Task ExecuteAsync()
        {
            try
            {
                await LoopAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested && !_caller.IsCancellationRequested)
            {
                // Stopped or superseded.
            }
            catch (Exception ex) when (!_caller.IsCancellationRequested)
            {
                Logger.LogError(ex, "Device sign-in on {Origin} failed", Origin);
                Publish(DeviceSignInStatus.Failed);
            }
            finally
            {
                if (_live is { } abandoned && MayWithdraw)
                {
                    await WithdrawAsync(abandoned).ConfigureAwait(false);
                }
                try
                {
                    _flow.End(this);
                }
                finally
                {
                    _stop.Dispose();
                    _completion.TrySetResult();
                }
            }
        }

        private async Task LoopAsync()
        {
            Publish(DeviceSignInStatus.GettingCode);
            _capability = await ReadCapabilityAsync().ConfigureAwait(false);
            if (_capability is { IsAvailable: false })
            {
                Logger.LogInformation("Device sign-in is not available on {Origin} ({State})", Origin, _capability.State);
                Publish(DeviceSignInStatus.Unavailable);
                return;
            }

            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (_live is null)
                {
                    if (!await StartCodeAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
                if (!await PollAsync(_live).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        private async Task<DeviceLoginCapability?> ReadCapabilityAsync()
        {
            try
            {
                return await CallAsync(token => _flow._api.GetCapabilityAsync(_server.BaseUri, token)).ConfigureAwait(false);
            }
            catch (Exception ex) when (Classify(ex) is not null)
            {
                Logger.LogDebug("Device sign-in capability of {Origin} could not be read: {Error}", Origin, ex.Message);
                return null;
            }
        }

        /// <summary>Asks for a new code (after the pending start delay); false when the run is over.</summary>
        private async Task<bool> StartCodeAsync()
        {
            if (_startDelay > TimeSpan.Zero)
            {
                var delay = _startDelay;
                _startDelay = TimeSpan.Zero;
                await WaitAsync(delay).ConfigureAwait(false);
            }

            DeviceLoginStart start;
            try
            {
                start = await CallAsync(token =>
                    _flow._api.StartAsync(_server.BaseUri, _flow._identity.DeviceName, ClientIdentity.Platform, token)).ConfigureAwait(false);
            }
            catch (Exception ex) when (Classify(ex) is { } failure)
            {
                return StartFailed(failure, ex);
            }

            _live = new LiveCode(start, Now, _flow._time.GetUtcNow());
            // Stopped while the code was issued: nobody will show it, and the finally block withdraws it.
            _stop.Token.ThrowIfCancellationRequested();
            _startBackoff = TimeSpan.Zero;
            _failingSince = null;
            Logger.LogInformation("Device sign-in code issued by {Origin}", Origin);
            Publish(DeviceSignInStatus.WaitingForApproval, _live.Code);
            return true;
        }

        private bool StartFailed(Failure failure, Exception ex)
        {
            switch (failure)
            {
                case Failure.Gone:
                    Logger.LogInformation("{Origin} has no device sign-in ({Error})", Origin, ex.Message);
                    Publish(DeviceSignInStatus.Unavailable);
                    return false;
                case Failure.Rejected:
                    Logger.LogWarning("{Origin} refused a device sign-in code: {Error}", Origin, ex.Message);
                    Publish(DeviceSignInStatus.Failed);
                    return false;
                default:
                    Logger.LogDebug("Getting a device sign-in code from {Origin} failed: {Error}", Origin, ex.Message);
                    NoteFailure(failure, StartUnreachableAfter, live: null);
                    _startBackoff = NextBackoff(_startBackoff, MinimumWait);
                    _startDelay = RetryAfterOr(ex, _startBackoff);
                    return true;
            }
        }

        /// <summary>Polls <paramref name="live"/> once and waits for the next poll; false when the run is over.</summary>
        private async Task<bool> PollAsync(LiveCode live)
        {
            DeviceLoginPoll answer;
            try
            {
                answer = await CallAsync(token => _flow._api.PollAsync(_server.BaseUri, live.DeviceCode, token)).ConfigureAwait(false);
            }
            catch (Exception ex) when (Classify(ex) is { } failure)
            {
                return await PollFailedAsync(live, failure, ex).ConfigureAwait(false);
            }

            // Stopped while the poll was in flight: drop the answer, even an approval.
            _stop.Token.ThrowIfCancellationRequested();
            switch (answer.Status)
            {
                case DeviceLoginStatus.Pending:
                    _failingSince = null;
                    live.Pending(answer);
                    Publish(live.Opened ? DeviceSignInStatus.Opened : DeviceSignInStatus.WaitingForApproval, live.Code);
                    await WaitOrReplaceAsync(live, live.Interval).ConfigureAwait(false);
                    return true;
                case DeviceLoginStatus.Approved when answer.Tokens is { } tokens && IsComplete(tokens):
                    _live = null;
                    await _flow._session.SignInWithTokensAsync(_server, tokens, _caller).ConfigureAwait(false);
                    Logger.LogInformation("Device sign-in on {Origin} approved", Origin);
                    Publish(DeviceSignInStatus.Approved);
                    return false;
                case DeviceLoginStatus.Approved:
                    Logger.LogWarning("Device sign-in on {Origin} was approved without tokens", Origin);
                    Publish(DeviceSignInStatus.Failed);
                    return false;
                case DeviceLoginStatus.Denied:
                    _live = null;
                    Logger.LogInformation("Device sign-in on {Origin} was declined", Origin);
                    Publish(DeviceSignInStatus.Denied);
                    return false;
                case DeviceLoginStatus.Expired or DeviceLoginStatus.Consumed or DeviceLoginStatus.Canceled:
                    Logger.LogDebug("Device sign-in code on {Origin} is {Status}; getting a new one", Origin, answer.Status);
                    await ReplaceAsync(live, stillLive: false).ConfigureAwait(false);
                    return true;
                default:
                    Logger.LogWarning("Device sign-in on {Origin} answered the unknown status {Status}", Origin, answer.Status);
                    Publish(DeviceSignInStatus.Failed);
                    return false;
            }
        }

        private async Task<bool> PollFailedAsync(LiveCode live, Failure failure, Exception ex)
        {
            switch (failure)
            {
                case Failure.Gone:
                    Logger.LogDebug("Device sign-in code on {Origin} is gone ({Error}); getting a new one", Origin, ex.Message);
                    await ReplaceAsync(live, stillLive: false).ConfigureAwait(false);
                    return true;
                case Failure.Rejected:
                    Logger.LogWarning("{Origin} refused a device sign-in poll: {Error}", Origin, ex.Message);
                    Publish(DeviceSignInStatus.Failed);
                    return false;
                default:
                    Logger.LogDebug("Polling device sign-in on {Origin} failed: {Error}", Origin, ex.Message);
                    live.Backoff = NextBackoff(live.Backoff, live.Interval);
                    NoteFailure(failure, PollUnreachableAfter, live);
                    await WaitOrReplaceAsync(live, RetryAfterOr(ex, live.Backoff)).ConfigureAwait(false);
                    return true;
            }
        }

        /// <summary>
        /// Waits <paramref name="wait"/> (clamped to the deadline, so the last call for a code is always a
        /// poll), or replaces the code when the deadline has passed.
        /// </summary>
        private async Task WaitOrReplaceAsync(LiveCode live, TimeSpan wait)
        {
            var now = Now;
            if (live.IsExpired(now))
            {
                Logger.LogDebug("Device sign-in code on {Origin} reached its deadline; getting a new one", Origin);
                await ReplaceAsync(live, stillLive: true).ConfigureAwait(false);
                return;
            }
            await WaitAsync(live.ClampToDeadline(wait, now)).ConfigureAwait(false);
        }

        /// <summary>Drops <paramref name="live"/> for a new code, withdrawing it first when the server may still hold it.</summary>
        private async Task ReplaceAsync(LiveCode live, bool stillLive)
        {
            var lived = Now - live.StartedAt;
            if (stillLive && MayWithdraw)
            {
                await WithdrawAsync(live).ConfigureAwait(false);
            }
            _live = null;
            if (lived < MinimumCodeLifetime)
            {
                _renewBackoff = NextBackoff(_renewBackoff, MinimumWait);
                _startDelay = _renewBackoff;
            }
            else
            {
                _renewBackoff = TimeSpan.Zero;
            }
        }

        /// <summary>Best-effort cancel, bounded to <see cref="CancelTimeout"/>; never throws.</summary>
        private async Task WithdrawAsync(LiveCode live)
        {
            using var abort = new CancellationTokenSource();
            try
            {
                await _flow._api.CancelAsync(_server.BaseUri, live.DeviceCode, abort.Token)
                    .WaitAsync(CancelTimeout, _flow._time).ConfigureAwait(false);
                Logger.LogDebug("Device sign-in code on {Origin} withdrawn", Origin);
            }
            catch (TimeoutException)
            {
                await abort.CancelAsync().ConfigureAwait(false);
                Logger.LogDebug("Withdrawing the device sign-in code on {Origin} timed out", Origin);
            }
            catch (Exception ex)
            {
                // Best effort: the server lets an abandoned code expire on its own.
                Logger.LogDebug("Withdrawing the device sign-in code on {Origin} failed: {Error}", Origin, ex.Message);
            }
        }

        /// <summary>
        /// Makes one call, canceled by <see cref="Stop"/> and bounded to <see cref="CallTimeout"/> on the
        /// injected clock (a call that completes at once starts no timer).
        /// </summary>
        private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var task = call(attempt.Token);
            try
            {
                return await task.WaitAsync(CallTimeout, _flow._time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await attempt.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        private void NoteFailure(Failure failure, TimeSpan unreachableAfter, LiveCode? live)
        {
            var now = Now;
            var code = live is not null && !live.IsExpired(now) ? live.Code : null;
            if (failure == Failure.RateLimited)
            {
                Publish(DeviceSignInStatus.TooManyRequests, code);
                return;
            }
            _failingSince ??= now;
            if (now - _failingSince.Value >= unreachableAfter)
            {
                Publish(DeviceSignInStatus.Unreachable, code);
            }
        }

        private Failure? Classify(Exception ex) => ex switch
        {
            SiloApiException { Status: 429 } => Failure.RateLimited,
            SiloApiException { Status: 404 or 410 or 501 } => Failure.Gone,
            SiloApiException { Status: 409, Code: CapabilityDisabled or CapabilityNotConfigured } => Failure.Gone,
            SiloApiException { Status: 408 or >= 500 } => Failure.Transient,
            SiloApiException => Failure.Rejected,
            HttpRequestException or JsonException or TimeoutException => Failure.Transient,
            // HttpClient's own timeout. Stop and the caller's cancellation end the run instead.
            OperationCanceledException when !_stop.IsCancellationRequested => Failure.Transient,
            _ => null,
        };

        private static bool IsComplete(TokenPair tokens) =>
            !string.IsNullOrEmpty(tokens.AccessToken) && !string.IsNullOrEmpty(tokens.RefreshToken) && tokens.User is not null;

        private static TimeSpan RetryAfterOr(Exception ex, TimeSpan fallback) =>
            ex is SiloApiException { RetryAfter: { } retryAfter } ? Clamp(retryAfter, MinimumWait, MaxRetryAfter) : fallback;

        private Task WaitAsync(TimeSpan delay) =>
            Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _flow._time, _stop.Token);

        private void Publish(DeviceSignInStatus status, DeviceSignInCode? code = null) =>
            _flow.Publish(this, new DeviceSignInState(status, code));
    }

    /// <summary>One live code: its deadline on this PC's clock, its poll interval and backoff.</summary>
    private sealed class LiveCode
    {
        private readonly DeviceLoginStart _start;
        private readonly DateTimeOffset _startedAtUtc;
        private readonly TimeSpan _initialDeadline;
        private readonly TimeSpan _baseInterval;

        public LiveCode(DeviceLoginStart start, TimeSpan now, DateTimeOffset utcNow)
        {
            _start = start;
            _startedAtUtc = utcNow;
            _initialDeadline = now + Seconds(start.ExpiresIn);
            _baseInterval = Seconds(start.Interval);
            StartedAt = now;
            Deadline = _initialDeadline;
            Interval = _baseInterval;
            Code = CreateCode();
        }

        public string DeviceCode => _start.DeviceCode;

        public TimeSpan StartedAt { get; }

        public TimeSpan Deadline { get; private set; }

        /// <summary>Wait between pending polls: <c>max(poll_after, interval)</c>.</summary>
        public TimeSpan Interval { get; private set; }

        public TimeSpan Backoff { get; set; }

        public bool Opened { get; private set; }

        public DeviceSignInCode Code { get; private set; }

        public bool IsExpired(TimeSpan now) => now >= Deadline;

        public TimeSpan ClampToDeadline(TimeSpan wait, TimeSpan now)
        {
            var left = Deadline - now;
            return wait < left ? wait : left;
        }

        public void Pending(DeviceLoginPoll answer)
        {
            Backoff = TimeSpan.Zero;
            Opened |= answer.Opened;
            var pollAfter = answer.PollAfter > 0 ? Seconds(answer.PollAfter) : TimeSpan.Zero;
            Interval = pollAfter > _baseInterval ? pollAfter : _baseInterval;

            // expires_at is server time. The start answer's expires_at/expires_in pair maps it onto this
            // PC's clock, so clock skew between the two cancels out. The deadline never moves earlier.
            if (answer.ExpiresAt is { } serverExpiry && _start.ExpiresAt != default)
            {
                var moved = _initialDeadline + (serverExpiry - _start.ExpiresAt);
                var latest = StartedAt + MaxLifetime;
                if (moved > latest)
                {
                    moved = latest;
                }
                if (moved > Deadline)
                {
                    Deadline = moved;
                    Code = CreateCode();
                }
            }
        }

        private DeviceSignInCode CreateCode() => new(
            DeviceSignInCode.FormatUserCode(_start.UserCode),
            DeviceSignInCode.FormatVerificationText(_start.VerificationUri, _start.VerificationUriComplete),
            _start.VerificationUriComplete,
            _startedAtUtc + (Deadline - StartedAt));

        private static TimeSpan Seconds(long seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 1, (long)MaxLifetime.TotalSeconds));
    }
}
