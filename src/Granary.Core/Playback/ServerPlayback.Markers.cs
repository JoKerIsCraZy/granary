using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// The marked parts of the playing file (docs/api-contract.md, 11.1): the watch detail's at first, then those of a read
// that names the file, which makes the server fill the markers it finds on demand.
public sealed partial class ServerPlayback
{
    private (string FileId, IReadOnlyList<PlaybackMarker> Markers)? _readMarkers;
    private readonly HashSet<string> _markersAsked = [];

    public event EventHandler? MarkersChanged;

    // The server's list: mpv reads none through remux or transcode routes (11.3).
    public IReadOnlyList<PlaybackChapter> Chapters => PlayingVersion is { } version && _context is { } context
        ? [.. version.Chapters.Where(c => c.EndSeconds > c.StartSeconds).OrderBy(c => c.StartSeconds)
            .Select(c => new PlaybackChapter(c.Title, c.StartSeconds, c.EndSeconds, Api.Images.ArtworkUrl.Resolve(context.Server.BaseUri, c.ThumbnailUrl)))]
        : [];

    public IReadOnlyList<PlaybackMarker> Markers => PlayingVersion is not { } version
        ? []
        : _readMarkers is { } read && read.FileId == version.FileId ? read.Markers : PlaybackMarker.Of(version);

    // Once per file of this playback, after its first frame: the read never holds up the start, and nothing but the
    // skip pills depends on it.
    private void ReadMarkers()
    {
        if (_context is not { } context || PlayingVersion is not { } version || !_markersAsked.Add(version.FileId))
        {
            return;
        }
        _ = ReadAsync(version.FileId);

        async Task ReadAsync(string fileId)
        {
            IReadOnlyList<PlaybackMarker>? markers;
            try
            {
                markers = await _starter.ReadMarkersAsync(context, fileId, CancellationToken.None);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogInformation("The markers of the playing file could not be read again: {Failure}", ApiFailureText.Describe(ex));
                return;
            }
            catch (Exception ex)
            {
                // Nobody waits for this read: what it did not expect is logged, and playback goes on.
                _logger.LogError(ex, "Reading the markers of the playing file failed unexpectedly");
                return;
            }
            if (markers is null)
            {
                return;
            }
            _dispatcher.Post(() =>
            {
                if (_stopping)
                {
                    return;
                }
                var before = Markers;
                _readMarkers = (fileId, markers);
                if (!before.SequenceEqual(Markers))
                {
                    MarkersChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }
}
