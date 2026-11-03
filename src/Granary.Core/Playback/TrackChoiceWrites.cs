using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>How a queued track choice ended.</summary>
public enum TrackWriteOutcome
{
    Saved,

    /// <summary>The server refused or did not answer in time; the choice is not remembered.</summary>
    Failed,

    /// <summary>Not sent, or given up: the server or profile that made it is no longer the active one.</summary>
    Dropped,
}

/// <summary>Who a queued choice belongs to: the saved server and the profile acting on it when it was made.</summary>
public sealed record TrackWriteOwner(string ServerEntryId, Uri Server, string ProfileId);

/// <summary>
/// The detail page's audio and subtitle picks on their way to the server (docs/api-contract.md, 8.8), one at a time in
/// the order they were made, as Apple's TrackSelectionPersistence sends them: Auto after a pick is never overtaken by
/// the pick's write. A playback reads the title's preferences once they are in (<see cref="SettleAsync"/>), so Play
/// right after a pick starts with what the page showed. A write that fails costs the remembered choice, never
/// playback: it is logged and not tried again.
/// </summary>
/// <remarks>
/// Each write belongs to the server and profile active when it was queued, as Apple's writer captures the signed-in
/// owner. It goes out only while they are still active, and bound to that profile, so the authentication handler sends
/// it as no other (<see cref="SiloRequestOptions.Profile"/>). Once they change, their writes are dropped: those still
/// queued are not sent, the one on its way is cancelled, and a start for the new owner waits for none of them.
/// </remarks>
public sealed class TrackChoiceWrites : IDisposable
{
    // A write that hangs must not hold the next one, nor a start, for the HTTP timeout.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly ISessionManager _session;
    private readonly TimeProvider _time;
    private readonly ILogger<TrackChoiceWrites> _logger;
    private readonly Lock _gate = new();

    // The writes of one owner, in order; another owner's start a chain of their own.
    private Task _tail = Task.CompletedTask;
    private TrackWriteOwner? _tailOwner;

    // Cancelled when the session's owner changes: the writes queued before are dropped.
    private CancellationTokenSource _ownerTurn = new();
    private TrackWriteOwner? _seenOwner;
    private int _waiting;

    public TrackChoiceWrites(ISessionManager session, TimeProvider time, ILogger<TrackChoiceWrites> logger)
    {
        _session = session;
        _time = time;
        _logger = logger;
        _seenOwner = Owner();
        session.Changed += OnSessionChanged;
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        lock (_gate)
        {
            _ownerTurn.Dispose();
        }
    }

    /// <summary>
    /// Sends <paramref name="write"/> after the writes its owner queued before, for the active server and profile; it is
    /// handed that owner to send to and as.
    /// </summary>
    /// <param name="kind">What is saved, for the log: "audio" or "subtitle".</param>
    public Task<TrackWriteOutcome> RunAsync(string kind, Func<TrackWriteOwner, CancellationToken, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (Owner() is not { } owner)
        {
            _logger.LogInformation("A {Kind} choice was not saved: no profile is active", kind);
            return Task.FromResult(TrackWriteOutcome.Dropped);
        }
        lock (_gate)
        {
            Interlocked.Increment(ref _waiting);
            var previous = owner == _tailOwner ? _tail : Task.CompletedTask;
            var run = RunAfterAsync(previous, kind, owner, _ownerTurn.Token, write);
            _tail = run;
            _tailOwner = owner;
            return run;
        }
    }

    /// <summary>
    /// Waits for the active owner's writes made so far, at most as long as one write may take; another owner's are not
    /// waited for. A start cannot ask for "no saved choice" itself (it resolves the audio from the saved preferences when
    /// it names none), so one that waited in vain may start with the choice saved before; that is logged.
    /// </summary>
    public async Task SettleAsync(CancellationToken cancellationToken)
    {
        var owner = Owner();
        Task tail;
        lock (_gate)
        {
            tail = owner is not null && owner == _tailOwner ? _tail : Task.CompletedTask;
        }
        try
        {
            await tail.WaitAsync(WriteTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Playback starts while {Count} track choice(s) are still being saved after {Seconds} s: it may start with the choice saved before",
                Volatile.Read(ref _waiting), WriteTimeout.TotalSeconds);
        }
    }

    private async Task<TrackWriteOutcome> RunAfterAsync(Task previous, string kind, TrackWriteOwner owner, CancellationToken ownerTurn,
        Func<TrackWriteOwner, CancellationToken, Task> write)
    {
        try
        {
            // Never faults: each write ends in an outcome.
            await previous.ConfigureAwait(false);
            if (ownerTurn.IsCancellationRequested || Owner() != owner)
            {
                return Dropped(kind);
            }
            using var timeout = new CancellationTokenSource(WriteTimeout, _time);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ownerTurn);
            var sending = write(owner, limit.Token);
            // An operation that does not stop when asked is left behind at its limit, so the next write goes on.
            _ = sending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await sending.WaitAsync(limit.Token).ConfigureAwait(false);
            return TrackWriteOutcome.Saved;
        }
        catch (Exception ex) when (ex is ProfileChangedException || (ex is OperationCanceledException && ownerTurn.IsCancellationRequested))
        {
            return Dropped(kind);
        }
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Saving the {Kind} choice failed: {Failure}", kind, ApiFailureText.Describe(ex));
            return TrackWriteOutcome.Failed;
        }
        catch (Exception ex)
        {
            // A preference, not playback: whatever went wrong, the page and the next write go on.
            _logger.LogError(ex, "Saving the {Kind} choice failed unexpectedly", kind);
            return TrackWriteOutcome.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    // Who is never logged: only that the owner changed.
    private TrackWriteOutcome Dropped(string kind)
    {
        _logger.LogInformation("A {Kind} choice was not saved: the server or profile changed before it went out", kind);
        return TrackWriteOutcome.Dropped;
    }

    private TrackWriteOwner? Owner() =>
        _session.ActiveServer is { } server && _session.ProfileId is { Length: > 0 } profile ? new TrackWriteOwner(server.Id, server.BaseUri, profile) : null;

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var owner = Owner();
        CancellationTokenSource dropped;
        lock (_gate)
        {
            if (owner == _seenOwner)
            {
                return;
            }
            _seenOwner = owner;
            dropped = _ownerTurn;
            _ownerTurn = new CancellationTokenSource();
        }
        // Left undisposed: writes queued before still hold its token, which stays cancelled.
        dropped.Cancel();
    }
}
