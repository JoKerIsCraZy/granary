using Granary.Core.Api.Downloads;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Core.Downloads;

// The pictures of a download's chapters (docs/architecture.md, "Playing a download"). The manifest keeps only each
// chapter's blur. While the server answers, the watch detail lists the file the download was made from (the manifest's
// media_file_id, also for a copy the server converted, whose timeline is the same) with its chapters' pictures.
public sealed partial class LocalPlayback
{
    // A chapter of the watch detail is the manifest's when it has the same index and starts within this of it.
    private const double ChapterStartTolerance = 1;

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
}
