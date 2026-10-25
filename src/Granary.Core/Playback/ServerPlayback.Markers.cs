using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// The marked parts of the playing file (docs/api-contract.md, 11.1): the watch detail's at first, then those of a read
// that names the file, which makes the server fill the markers it finds on demand.
public sealed partial class ServerPlayback
{
    private (string FileId, IReadOnlyList<PlaybackMarker> Markers)? _readMarkers;
    private readonly HashSet<string> _markersAsked = [];

    // Counts the viewer's saves: a read that was on its way before one names the markers as they were.
    private int _markerSaves;

    public event EventHandler? MarkersChanged;

    // The server's list: mpv reads none through remux or transcode routes (11.3).
    public IReadOnlyList<PlaybackChapter> Chapters => PlayingVersion is { } version && _context is { } context
        ? [.. version.Chapters.Where(c => c.EndSeconds > c.StartSeconds).OrderBy(c => c.StartSeconds)
            .Select(c => new PlaybackChapter(c.Title, c.StartSeconds, c.EndSeconds, Api.Images.ArtworkUrl.Resolve(context.Server.BaseUri, c.ThumbnailUrl)))]
        : [];

    public async Task SaveMarkersAsync(IReadOnlyDictionary<string, WatchMarker?> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_context is not { } context || PlayingVersion is not { } version)
        {
            throw new InvalidOperationException("No file plays.");
        }
        string fileId = version.FileId;
        var saved = await _markers.SetAsync(context.Server.BaseUri, fileId, changes, cancellationToken);
        if (_stopping || PlayingVersion?.FileId != fileId)
        {
            return;
        }
        // The answer is the file's markers as they are now: the skip pills and the seek bar take them at once.
        var before = Markers;
        _markerSaves++;
        _readMarkers = (fileId, PlaybackMarker.Of(saved.AsVersion(fileId), context.Server.BaseUri));
        _logger.LogInformation("Saved {Count} marker change(s) for the playing file", changes.Count);
        if (!before.SequenceEqual(Markers))
        {
            MarkersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<PlaybackMarker> Markers => PlayingVersion is not { } version
        ? []
        : _readMarkers is { } read && read.FileId == version.FileId ? read.Markers : PlaybackMarker.Of(version, _context?.Server.BaseUri);

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
            int saves = _markerSaves;
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
                if (_stopping || saves != _markerSaves)
                {
                    return;
                }
                var before = Markers;
                _readMarkers = (fileId, markers);
                // A renewed picture counts too: the seek bar shows it, and the signed address of the one before expires.
                if (!before.SequenceEqual(Markers) || !before.Select(m => m.ThumbnailUrl).SequenceEqual(Markers.Select(m => m.ThumbnailUrl)))
                {
                    MarkersChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }
}
