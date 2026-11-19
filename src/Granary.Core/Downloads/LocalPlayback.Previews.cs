using Granary.Core.Api.Downloads;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Core.Downloads;

// The pictures of a download's chapters and seek bar (docs/architecture.md, "Playing a download"). The manifest keeps only
// each chapter's blur. While the server answers, the watch detail lists the file the download was made from (the
// manifest's media_file_id, also for a copy the server converted, whose timeline is the same): its chapters' pictures and,
// when it has them, its seek-bar previews, read as the download's own account and profile. Previews kept with the download
// (DownloadAssets.Previews.cs) are those same pictures: they show at once, online or not, and the server is not asked for them.
public sealed partial class LocalPlayback
{
    // A chapter of the watch detail is the manifest's when it has the same index and starts within this of it.
    private const double ChapterStartTolerance = 1;

    private readonly PlayerTrickplay? _keptPreviews;
    private PlayerTrickplay? _trickplay;
    private TrickplayReads? _trickplayReads;

    public event EventHandler? TrickplayChanged;

    /// <summary>
    /// The previews of the file the download was made from: the ones kept with it, else the server's while it answers; null
    /// without.
    /// </summary>
    public PlayerTrickplay? Trickplay => _trickplay;

    /// <summary>
    /// The manifest's chapters, each with its blur, and with its picture where the watch detail lists the same chapter: after
    /// a rescan the server's list can differ from the one the download kept.
    /// </summary>
    public IReadOnlyList<PlaybackChapter> Chapters
    {
        get
        {
            var listed = SourceVersion?.Chapters ?? [];
            return
            [
                .. _manifest.Chapters.Where(c => c.EndSeconds > c.StartSeconds).OrderBy(c => c.StartSeconds)
                    .Select(c => new PlaybackChapter(c.Title ?? "", c.StartSeconds, c.EndSeconds, PictureOf(c, listed), c.ThumbnailThumbhash)),
            ];
        }
    }

    // The watch detail's version that is the file the download was made from; null without the detail.
    private WatchFileVersion? SourceVersion =>
        _detail?.Versions.FirstOrDefault(v => v.FileId == (_manifest.MediaFileId ?? _download.Record.MediaFileId));

    private Uri? PictureOf(OfflineChapter chapter, IReadOnlyList<WatchChapter> listed) =>
        _server is { } server && listed.FirstOrDefault(c => c.Index == chapter.Index
            && Math.Abs(c.StartSeconds - chapter.StartSeconds) <= ChapterStartTolerance) is { ThumbnailUrl: { } url }
            ? ArtworkUrl.Resolve(server, url)
            : null;

    // Once the watch detail came: the previews of the file the download was made from, when it has them. Without the server
    // there is no detail, and nothing is asked. Kept ones need neither.
    private void ReadTrickplay()
    {
        if (!_stopping && _keptPreviews is { } kept)
        {
            _trickplay = kept;
            TrickplayChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_stopping || _server is not { } server || SourceVersion is not { TrickplayAvailable: true } version)
        {
            return;
        }
        var owner = _download.Record.Scope;
        string contentId = _download.Record.MediaItemId;
        string fileId = version.FileId;
        _trickplayReads?.Dispose();
        _trickplayReads = new TrickplayReads(async cancellationToken =>
        {
            var manifest = await _watch.GetTrickplayAsAccountProfileAsync(server, owner.AccountId, owner.ProfileId, contentId, fileId, cancellationToken)
                .ConfigureAwait(false);
            return PlayerTrickplay.Of(manifest, server);
        }, trickplay =>
        {
            _trickplay = trickplay;
            TrickplayChanged?.Invoke(this, EventArgs.Empty);
        }, _dispatcher, _time, _logger);
        _trickplayReads.Start();
    }

    private void StopTrickplay()
    {
        _trickplayReads?.Dispose();
        _trickplayReads = null;
    }
}
