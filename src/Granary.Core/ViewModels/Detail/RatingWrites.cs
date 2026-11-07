using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Detail;

/// <summary>Whose rating of what: the server, the profile the title was read for, and the title.</summary>
public sealed record RatingTarget(Uri Server, string ProfileId, string ItemId);

/// <summary>How a title's writes ended once none was left.</summary>
/// <param name="Kept">The rating the server keeps, 0 for none; null when none of these writes stuck, so it keeps what it had.</param>
/// <param name="Failure">Why the last click did not stick, when the server refused it; null when it stuck or was dropped.</param>
public sealed record RatingSettled(int? Kept, Exception? Failure);

/// <summary>A title's writes still on their way: the newest click, and how they will end.</summary>
public sealed record RatingInFlight(int Latest, Task<RatingSettled> Settled);

/// <summary>
/// The profile's star ratings on their way to the server (docs/api-contract.md, 7.11), one writer per title and profile
/// for the whole app. A writer sends one write at a time; a click made while one is on its way waits, and only the newest
/// waiting click is sent, so quick clicks end on the last one. A title's page opened again while its writes are on their
/// way joins them (<see cref="InFlight"/>) instead of starting a second writer; one whose read crossed their end shows
/// what they left (<see cref="KeptSince"/>).
/// </summary>
/// <remarks>
/// Each write is bound to the profile in its <see cref="RatingTarget"/>, so the authentication handler never sends it as
/// another (<see cref="ProfileChangedException"/>); the clicks waiting behind such a write are dropped. Writes are
/// non-retryable: one that fails is not sent again. The app holds no page: a page asks when it loads, and awaits the end
/// of the writes it joined or made.
/// </remarks>
public sealed class RatingWrites(CatalogApi catalog, TimeProvider time, ILogger<RatingWrites> logger, ViewerStateChanges? changes = null)
{
    // Long enough for a title's read that began before its writes ended to see them; any later read sees them itself.
    private static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<RatingTarget, Writer> _live = [];
    private readonly Dictionary<RatingTarget, Remembered> _ended = [];

    // Counts the ends remembered, so a page knows which came after its read began.
    private long _ends;

    /// <summary>Where the writes' ends stand: take it before reading a title, for <see cref="KeptSince"/>.</summary>
    public long Mark
    {
        get
        {
            lock (_gate)
            {
                return _ends;
            }
        }
    }

    /// <summary>The title's writes still on their way, or null when there are none.</summary>
    public RatingInFlight? InFlight(RatingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            return _live.TryGetValue(target, out var writer) ? new RatingInFlight(writer.Latest, writer.Settled.Task) : null;
        }
    }

    /// <summary>
    /// The rating the title's writes left, when they ended after <paramref name="mark"/> and within the last minute: newer
    /// than a read that began at the mark. Null otherwise.
    /// </summary>
    public int? KeptSince(RatingTarget target, long mark)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            Forget();
            return _ended.TryGetValue(target, out var ended) && ended.End > mark ? ended.Rating : null;
        }
    }

    /// <summary>Rates the title with <paramref name="rating"/> stars, 0 to clear it, and answers once its writes are all done.</summary>
    public Task<RatingSettled> RateAsync(RatingTarget target, int rating)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(rating);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rating, RatingSet.Max);
        Writer writer;
        bool starts;
        lock (_gate)
        {
            if (!_live.TryGetValue(target, out var live))
            {
                live = new Writer(target);
                _live.Add(target, live);
            }
            writer = live;
            writer.Waiting = rating;
            writer.Latest = rating;
            starts = !writer.IsWriting;
            writer.IsWriting = true;
        }
        if (starts)
        {
            _ = SendAsync(writer);
        }
        return writer.Settled.Task;
    }

    // Sends the waiting clicks until none is left. No one awaits this task, so whatever throws, it ends the writer and lets
    // its pages go on. It keeps the caller's context (no ConfigureAwait(false)): the pages and the followers of
    // ViewerStateChanges expect the UI thread.
    private async Task SendAsync(Writer writer)
    {
        try
        {
            Exception? failure = null;
            while (Next(writer, failure) is { } rating)
            {
                failure = await WriteAsync(writer, rating);
            }
        }
        catch (Exception ex)
        {
            Log(l => l.LogError("Rating item {ItemId} ended unexpectedly: {Failure}", writer.Target.ItemId, ex.GetType().Name));
        }
        finally
        {
            lock (_gate)
            {
                End(writer, failure: null);
            }
        }
    }

    // One write; what the page words when it is the last click: the request's failure, else null (it stuck, or was dropped
    // with the clicks behind it).
    private async Task<Exception?> WriteAsync(Writer writer, int rating)
    {
        var target = writer.Target;
        try
        {
            if (rating == 0)
            {
                await catalog.ClearRatingAsync(target.Server, target.ItemId, target.ProfileId);
            }
            else
            {
                await catalog.SetRatingAsync(target.Server, target.ItemId, target.ProfileId, rating);
            }
        }
        catch (ProfileChangedException)
        {
            // Another profile is active now: none of these clicks may reach it.
            Log(l => l.LogInformation("Rating item {ItemId} was dropped: the profile changed", target.ItemId));
            Drop(writer);
            return null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            Log(l => l.LogWarning("Rating item {ItemId} failed: {Failure}", target.ItemId, ApiFailureText.Describe(ex)));
            return ex;
        }
        catch (Exception ex)
        {
            // Not a failure of the request: the clicks waiting behind it are dropped rather than left hanging.
            Log(l => l.LogError("Rating item {ItemId} ended unexpectedly: {Failure}", target.ItemId, ex.GetType().Name));
            Drop(writer);
            return null;
        }
        lock (_gate)
        {
            writer.Kept = rating;
        }
        Report(target);
        return null;
    }

    // The next click to send; null once none is left, and then the writer has ended with what the server keeps and, when
    // the last click was refused, why. A click is skipped only when the server is known to keep it already: before a write
    // of this writer stuck, the page's own value may be stale.
    private int? Next(Writer writer, Exception? failure)
    {
        lock (_gate)
        {
            while (writer.Waiting is { } rating)
            {
                writer.Waiting = null;
                if (writer.Kept != rating)
                {
                    return rating;
                }
                failure = null;
            }
            End(writer, failure);
            return null;
        }
    }

    // Once only, under the lock. A click that comes after it starts a new writer.
    private void End(Writer writer, Exception? failure)
    {
        if (writer.Settled.Task.IsCompleted)
        {
            return;
        }
        writer.Waiting = null;
        writer.IsWriting = false;
        if (_live.TryGetValue(writer.Target, out var live) && ReferenceEquals(live, writer))
        {
            _live.Remove(writer.Target);
        }
        writer.Settled.TrySetResult(new RatingSettled(writer.Kept, failure));
        if (writer.Kept is { } kept)
        {
            Forget();
            _ended[writer.Target] = new Remembered(kept, ++_ends, time.GetUtcNow());
        }
    }

    private void Forget()
    {
        var now = time.GetUtcNow();
        foreach (var target in _ended.Where(entry => now - entry.Value.At >= RememberFor).Select(entry => entry.Key).ToList())
        {
            _ended.Remove(target);
        }
    }

    private void Drop(Writer writer)
    {
        lock (_gate)
        {
            writer.Waiting = null;
        }
    }

    // A follower that throws must not turn a write the server kept into a failure.
    private void Report(RatingTarget target)
    {
        try
        {
            changes?.Report();
        }
        catch (Exception ex)
        {
            Log(l => l.LogError("A follower of the viewer's changes failed after rating item {ItemId}: {Failure}", target.ItemId, ex.GetType().Name));
        }
    }

    // A log line that fails is lost rather than ending the writer.
    private void Log(Action<ILogger> write)
    {
        try
        {
            write(logger);
        }
        catch (Exception)
        {
            // Nowhere left to tell.
        }
    }

    private sealed class Writer(RatingTarget target)
    {
        public RatingTarget Target { get; } = target;

        /// <summary>What the server keeps after this writer's last write that stuck; null until one has.</summary>
        public int? Kept { get; set; }

        /// <summary>The newest click not sent yet.</summary>
        public int? Waiting { get; set; }

        /// <summary>The newest click, sent or not: what a page shows while the writes are on their way.</summary>
        public int Latest { get; set; }

        public bool IsWriting { get; set; }

        public TaskCompletionSource<RatingSettled> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Remembered(int Rating, long End, DateTimeOffset At);
}
