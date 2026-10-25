using Granary.Core.Api;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Seek-bar previews of the playing file (docs/api-contract.md, 11.4), read once it plays and again before their
// addresses expire, as the web player keeps them.
public sealed partial class ServerPlayback
{
    // Read again this long before the sheets' addresses stop working, and at the soonest after a minute.
    private static readonly TimeSpan TrickplayRefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrickplayRetry = TimeSpan.FromMinutes(1);

    private PlayerTrickplay? _trickplay;
    private string? _trickplayFile;
    private ITimer? _trickplayRefresh;

    public event EventHandler? TrickplayChanged;

    public PlayerTrickplay? Trickplay => _trickplay is { } trickplay && trickplay.FileId == PlayingVersion?.FileId ? trickplay : null;

    // After the first frame of a file that has previews; another file reads its own.
    private void ReadTrickplay()
    {
        if (PlayingVersion is not { TrickplayAvailable: true } version || version.FileId == _trickplayFile)
        {
            return;
        }
        _trickplayFile = version.FileId;
        _ = ReadTrickplayAsync(version.FileId);
    }

    private async Task ReadTrickplayAsync(string fileId)
    {
        if (_context is not { } context)
        {
            return;
        }
        TimeSpan? again;
        PlayerTrickplay? trickplay = null;
        try
        {
            trickplay = await _starter.ReadTrickplayAsync(context, fileId, CancellationToken.None);
            again = trickplay is null ? null : Max(trickplay.ExpiresAt - _time.GetUtcNow() - TrickplayRefreshMargin, TrickplayRetry);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // The file has no previews after all.
            again = null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The seek-bar previews could not be read: {Failure}", ApiFailureText.Describe(ex));
            again = PlaybackProgress.IsTransient(ex) ? TrickplayRetry : null;
        }
        catch (Exception ex)
        {
            // Previews are a convenience: whatever went wrong, the title plays on without them.
            _logger.LogError(ex, "Reading the seek-bar previews failed unexpectedly");
            again = null;
        }
        _dispatcher.Post(() =>
        {
            if (_stopping || _trickplayFile != fileId)
            {
                return;
            }
            if (trickplay is not null)
            {
                _trickplay = trickplay;
                TrickplayChanged?.Invoke(this, EventArgs.Empty);
            }
            _trickplayRefresh?.Dispose();
            _trickplayRefresh = again is { } wait
                ? _time.CreateTimer(_ => _dispatcher.Post(() =>
                {
                    if (!_stopping && _trickplayFile == fileId)
                    {
                        _ = ReadTrickplayAsync(fileId);
                    }
                }), null, wait, Timeout.InfiniteTimeSpan)
                : null;
        });

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    private void StopTrickplay()
    {
        _trickplayRefresh?.Dispose();
        _trickplayRefresh = null;
    }
}
