using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// The detail page's audio and subtitle picks on their way to the server (docs/api-contract.md, 8.8), one at a time in
/// the order they were made, as Apple's TrackSelectionPersistence sends them: Auto after a pick is never overtaken by
/// the pick's write. A playback reads the title's preferences once they are in (<see cref="SettleAsync"/>), so Play
/// right after a pick starts with what the page showed. A write that fails costs the remembered choice, never
/// playback: it is logged and not tried again.
/// </summary>
public sealed class TrackChoiceWrites(TimeProvider time, ILogger<TrackChoiceWrites> logger)
{
    // A write that hangs must not hold the next one, nor a start, for the HTTP timeout.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _waiting;

    /// <summary>Sends <paramref name="write"/> after the writes before it; true once the server took it.</summary>
    /// <param name="kind">What is saved, for the log: "audio" or "subtitle".</param>
    public Task<bool> RunAsync(string kind, Func<CancellationToken, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_gate)
        {
            Interlocked.Increment(ref _waiting);
            var run = RunAfterAsync(_tail, kind, write);
            _tail = run;
            return run;
        }
    }

    /// <summary>
    /// Waits for the writes made so far, at most as long as one write may take. A start cannot ask for "no saved choice"
    /// itself (it resolves the audio from the saved preferences when it names none), so one that waited in vain may start
    /// with the choice saved before; that is logged.
    /// </summary>
    public async Task SettleAsync(CancellationToken cancellationToken)
    {
        Task tail;
        lock (_gate)
        {
            tail = _tail;
        }
        try
        {
            await tail.WaitAsync(WriteTimeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Playback starts while {Count} track choice(s) are still being saved after {Seconds} s: it may start with the choice saved before",
                Volatile.Read(ref _waiting), WriteTimeout.TotalSeconds);
        }
    }

    private async Task<bool> RunAfterAsync(Task previous, string kind, Func<CancellationToken, Task> write)
    {
        try
        {
            // Never faults: each write ends in true or false.
            await previous.ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(WriteTimeout, time);
            await write(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogWarning("Saving the {Kind} choice failed: {Failure}", kind, ApiFailureText.Describe(ex));
            return false;
        }
        catch (Exception ex)
        {
            // A preference, not playback: whatever went wrong, the page and the next write go on.
            logger.LogError(ex, "Saving the {Kind} choice failed unexpectedly", kind);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }
}
