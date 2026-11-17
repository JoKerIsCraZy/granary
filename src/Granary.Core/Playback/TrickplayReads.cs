using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// The reads of one file's seek-bar previews (docs/api-contract.md, 11.4), as the web player keeps them: read once, and
/// again 5 minutes before their addresses expire, or a minute after a failure that may pass. A stream's
/// (<see cref="ServerPlayback"/>) and a download's while the server answers (<c>LocalPlayback</c>) share them. Disposing
/// ends them: a read on its way is called off, is not taken and starts no other.
/// </summary>
public sealed class TrickplayReads : IDisposable
{
    /// <summary>Read again this long before the sheets' addresses stop working.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>The soonest read again, and the wait after a failure that may pass.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    private readonly Func<CancellationToken, Task<PlayerTrickplay?>> _read;
    private readonly Action<PlayerTrickplay> _take;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // Calls off the read on its way once the reads end: nobody needs its answer.
    private readonly CancellationTokenSource _cancel = new();
    private ITimer? _refresh;
    private bool _ended;

    /// <param name="read">Reads the previews; null when they cannot be shown.</param>
    /// <param name="take">Gets each read's previews, on the UI thread.</param>
    public TrickplayReads(Func<CancellationToken, Task<PlayerTrickplay?>> read, Action<PlayerTrickplay> take, IUiDispatcher dispatcher,
        TimeProvider time, ILogger logger)
    {
        _read = read;
        _take = take;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>The first read; those after it follow by themselves.</summary>
    public void Start() => _ = ReadAsync();

    public void Dispose()
    {
        if (_ended)
        {
            return;
        }
        _ended = true;
        _refresh?.Dispose();
        _refresh = null;
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private async Task ReadAsync()
    {
        if (_ended)
        {
            return;
        }
        var cancellationToken = _cancel.Token;
        TimeSpan? again;
        PlayerTrickplay? trickplay = null;
        try
        {
            trickplay = await _read(cancellationToken).ConfigureAwait(false);
            again = trickplay is null ? null : Max(trickplay.ExpiresAt - _time.GetUtcNow() - RefreshMargin, Retry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The play ended meanwhile.
            return;
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // The file has no previews after all.
            again = null;
        }
        catch (ProfileChangedException)
        {
            // A download's previews are its own profile's; another acts on the server now, so they are not asked for again.
            _logger.LogInformation("The seek-bar previews were not read: the server acts for another profile now");
            again = null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The seek-bar previews could not be read: {Failure}", ApiFailureText.Describe(ex));
            again = PlaybackProgress.IsTransient(ex) ? Retry : null;
        }
        catch (Exception ex)
        {
            // Previews are a convenience: whatever went wrong, the title plays on without them.
            _logger.LogError(ex, "Reading the seek-bar previews failed unexpectedly");
            again = null;
        }
        _dispatcher.Post(() => Take(trickplay, again));

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    private void Take(PlayerTrickplay? trickplay, TimeSpan? again)
    {
        if (_ended)
        {
            return;
        }
        if (trickplay is not null)
        {
            _take(trickplay);
            // Whoever took them may have ended the reads (a handler that stops the play).
            if (_ended)
            {
                return;
            }
        }
        _refresh?.Dispose();
        _refresh = again is { } wait
            ? _time.CreateTimer(_ => _dispatcher.Post(() =>
            {
                if (!_ended)
                {
                    _ = ReadAsync();
                }
            }), null, wait, Timeout.InfiniteTimeSpan)
            : null;
    }
}
