using System.Globalization;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

// The tracks of a download (docs/architecture.md, "Playing a download"). Audio tracks, chapters and marked parts are the
// manifest's. Subtitles are the files kept beside the media (the manifest's list, each read into the player when it is
// picked, as Silo's apps side-load them) and, once the file plays, the tracks the file carries itself, which no manifest
// lists. The subtitle a title starts with is the one a stream would start with: the title page's pick, else the automatic
// choice from the profile's preference (SubtitleAutoSelection), over these tracks.
public sealed partial class LocalPlayback
{
    private const string SidecarPrefix = "local:sidecar:";
    private const string StreamPrefix = "local:stream:";

    private readonly List<LocalSubtitle> _subtitles = [];

    // The sidecars read, or being read, into the load, by track: picking one again shares its read.
    private readonly Dictionary<string, Task<int?>> _readSidecars = [];

    private IReadOnlyList<WatchAudioTrack>? _audioTracks;
    private int? _audio;
    private int? _pendingAudio;
    private string? _subtitle;

    // The viewer picked a subtitle, or none, before the file played: the start's own choice stands back.
    private bool _subtitlePicked;
    private PrePlayChoice? _choice;
    private WatchDetail? _detail;

    public event EventHandler? TracksChanged;

    public IReadOnlyList<WatchAudioTrack> AudioTracks => _audioTracks ??=
    [
        .. _manifest.AudioTracks.Select(track => new WatchAudioTrack
        {
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            Layout = track.Layout,
            Channels = track.Channels ?? 0,
            Default = track.Default,
        }),
    ];

    public int? AudioTrack => _pendingAudio ?? _audio;

    public IReadOnlyList<SubtitleInventoryItem> SubtitleTracks => [.. _subtitles.Select(s => s.Item)];

    public string? SubtitleTrack => _subtitle;

    public IReadOnlyList<PlaybackChapter> Chapters =>
    [
        .. _manifest.Chapters.Where(c => c.EndSeconds > c.StartSeconds).OrderBy(c => c.StartSeconds)
            .Select(c => new PlaybackChapter(c.Title ?? "", c.StartSeconds, c.EndSeconds, null)),
    ];

    /// <summary>
    /// The marked parts the manifest lists, as a stream's (<see cref="PlaybackMarker.Of"/>): the first intro, and every recap,
    /// credits and preview part.
    /// </summary>
    public IReadOnlyList<PlaybackMarker> Markers
    {
        get
        {
            var parts = _manifest.MarkerSegments.Where(m => m.EndSeconds > m.StartSeconds).ToList();
            var intro = parts.Where(m => m.Kind == WatchMarkerKinds.Intro).Take(1);
            var others = parts.Where(m => m.Kind is WatchMarkerKinds.Recap or WatchMarkerKinds.Credits or WatchMarkerKinds.Preview);
            return [.. intro.Concat(others).OrderBy(m => m.StartSeconds).Select(m => new PlaybackMarker(m.Kind, m.StartSeconds, m.EndSeconds))];
        }
    }

    /// <summary>Not for a download: its markers are the manifest's, and editing them needs a file the server plays.</summary>
    public Task SaveMarkersAsync(IReadOnlyDictionary<string, WatchMarker?> changes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A download's markers cannot be edited.");

    public void SelectAudio(int ordinal)
    {
        if (!CanChangeTracks || ordinal < 0 || ordinal >= AudioTracks.Count || ordinal == AudioTrack)
        {
            return;
        }
        if (!_started)
        {
            // The file has not started: the pick is switched to once it plays.
            _pendingAudio = ordinal;
            TracksChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        SwitchAudio(ordinal);
    }

    public void SelectSubtitle(string? trackId)
    {
        if (!CanChangeTracks || (trackId is not null && _subtitles.All(s => s.Item.TrackId != trackId)))
        {
            return;
        }
        _subtitlePicked = true;
        if (trackId == _subtitle)
        {
            return;
        }
        _subtitle = trackId;
        TracksChanged?.Invoke(this, EventArgs.Empty);
        if (_started)
        {
            ShowSubtitle();
        }
    }

    private bool CanChangeTracks => !_stopping && Status.Phase is not (ServerPlaybackPhase.Failed or ServerPlaybackPhase.Ended);

    // The file is every track in it: the switch is the player's own. mpv counts the audio streams from 1.
    private void SwitchAudio(int ordinal)
    {
        _pendingAudio = null;
        if (!_engine.SetAudioStream(ordinal + 1))
        {
            _logger.LogWarning("Download {DownloadId} has no audio stream {Stream}; the track before plays on", _download.Record.DownloadId, ordinal + 1);
        }
        else
        {
            _audio = ordinal;
        }
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    // Before the load: the audio the file starts with, and what the start's subtitle choice is made from. The sidecars are
    // listed at once; the file's own subtitle tracks follow with its first frame.
    private void TakeStartTracks(PlayRequest request, WatchDetail? detail)
    {
        _choice = request.Choice;
        _detail = detail;
        _audio = StartAudio();
        ListSidecars();
    }

    // The title page's audio pick, where it names a track of this file; else the one the server chose for the download.
    private int? StartAudio()
    {
        int count = AudioTracks.Count;
        if (count == 0)
        {
            return null;
        }
        if (_pendingAudio is { } pending && pending < count)
        {
            _pendingAudio = null;
            return pending;
        }
        _pendingAudio = null;
        if (_choice is { AudioTrack: { } picked } choice && picked >= 0)
        {
            bool sameFile = choice.FileId == (_manifest.MediaFileId ?? _download.Record.MediaFileId);
            var pickedTrack = _detail?.Versions.FirstOrDefault(v => v.FileId == choice.FileId)?.AudioTracks.ElementAtOrDefault(picked);
            // A copy the server prepared can carry fewer tracks than the file it was made from: then the pick is found by its language.
            if (sameFile && picked < count && (pickedTrack is null || SameLanguage(pickedTrack.Language, AudioTracks[picked].Language)))
            {
                return picked;
            }
            if (pickedTrack is not null && AudioTracks.Select((track, ordinal) => (track, ordinal))
                    .Where(t => SameLanguage(t.track.Language, pickedTrack.Language)).Select(t => (int?)t.ordinal).FirstOrDefault() is { } byLanguage)
            {
                return byLanguage;
            }
        }
        return _manifest.SelectedAudioTrackIndex is { } selected && selected >= 0 && selected < count ? selected : 0;
    }

    private static bool SameLanguage(string? first, string? second) => TrackLanguages.Key(first) == TrackLanguages.Key(second);

    // The subtitle files kept with the download, in the manifest's order; one that never arrived is left out, as in Silo's apps.
    private void ListSidecars()
    {
        _subtitles.Clear();
        for (int index = 0; index < _manifest.Subtitles.Count; index++)
        {
            var subtitle = _manifest.Subtitles[index];
            if (DownloadAssets.SubtitleFile(_download.Record.DownloadId, subtitle) is not { } file || !_files.Contains(file.Name))
            {
                continue;
            }
            string id = SidecarPrefix + index.ToString(CultureInfo.InvariantCulture);
            _subtitles.Add(new LocalSubtitle(new SubtitleInventoryItem
            {
                TrackId = id,
                CombinedIndex = _subtitles.Count,
                Source = SourceOf(file.Reference),
                Codec = subtitle.Format,
                Language = subtitle.Language,
                Label = subtitle.Title,
                Forced = subtitle.Forced,
                HearingImpaired = subtitle.HearingImpaired,
                Delivery = SubtitleDeliveries.Sidecar,
            }, Path.Combine(_download.Folder, file.Name), null));
        }
    }

    // external:0, embedded:2 (a track the server took out of the file it prepared) or downloaded:17.
    private static string SourceOf(string reference) => reference[..Math.Max(0, reference.IndexOf(':'))] switch
    {
        SubtitleSources.External => SubtitleSources.External,
        SubtitleSources.Downloaded => SubtitleSources.Downloaded,
        _ => SubtitleSources.Embedded,
    };

    // With the first frame: the tracks the file carries itself join the list, and the start's subtitle shows.
    private void ShowStartSubtitle()
    {
        foreach (var track in _engine.ReadSubtitleTracks().Where(t => !t.IsExternal))
        {
            _subtitles.Add(new LocalSubtitle(new SubtitleInventoryItem
            {
                TrackId = StreamPrefix + track.Stream.ToString(CultureInfo.InvariantCulture),
                CombinedIndex = _subtitles.Count,
                Source = SubtitleSources.Embedded,
                Codec = track.Codec,
                Language = track.Language,
                Label = track.Title,
                Forced = track.IsForced,
                Default = track.IsDefault,
                HearingImpaired = track.IsHearingImpaired,
                Delivery = SubtitleDeliveries.Sidecar,
            }, null, track.Stream));
        }
        if (_pendingAudio is { } audio)
        {
            SwitchAudio(audio);
        }
        if (!_subtitlePicked)
        {
            _subtitle = StartSubtitle()?.Item.TrackId;
        }
        TracksChanged?.Invoke(this, EventArgs.Empty);
        ShowSubtitle();
    }

    // What a stream would start with. The title page's pick names a track of the server's listing: the track here that is
    // like it shows (Off is a pick too). Without one, the automatic choice from the profile's preference, which the title's
    // state on the server carries; without the server, the preference the title had when the server last said
    // (DownloadRecord.SubtitlePreference), and for a download never played with the server, the server's default: none.
    private LocalSubtitle? StartSubtitle()
    {
        if (_choice is { SubtitleTrack: { } picked } choice)
        {
            if (picked < 0)
            {
                return null;
            }
            var listed = _detail?.Versions.FirstOrDefault(v => v.FileId == choice.FileId) is { } version
                ? SubtitleAutoSelection.FromCatalog(version.SubtitleTracks).FirstOrDefault(c => c.SelectionIndex == picked)
                : null;
            if (listed is not null && LikeHere(listed) is { } here)
            {
                return here;
            }
        }
        var candidates = _subtitles.Select((s, index) => new SubtitleCandidate(index, s.Item.Language, s.Item.Codec, s.Item.Label, s.Item.Forced,
            s.Item.HearingImpaired, s.Item.Source, NeedsBurnIn: false)).ToList();
        string? audioLanguage = AudioTrack is { } audio && audio < AudioTracks.Count ? AudioTracks[audio].Language : null;
        var preference = _detail is { } detail
            ? new SubtitlePreference(PreferredLanguage(detail), detail.SubtitleMode, detail.ShowForcedSubtitles, audioLanguage,
                detail.EffectiveSubtitleTrackSignature)
            : _download.Record.SubtitlePreference is { } kept
                ? new SubtitlePreference(kept.Language, kept.Mode, kept.ShowForced, audioLanguage, kept.Signature)
                : new SubtitlePreference(null, SubtitlePreferenceModes.Auto, ShowForced: true, audioLanguage);
        return SubtitleAutoSelection.Resolve(candidates, preference) is { } resolved ? _subtitles[resolved.SelectionIndex] : null;
    }

    private static string? PreferredLanguage(WatchDetail detail) =>
        string.IsNullOrWhiteSpace(detail.EffectiveSubtitleLanguage) ? null : detail.EffectiveSubtitleLanguage;

    // The title's subtitle preference as the server just said it stays with the download, for a play without the server.
    // It is the title's own: the server resolves it from the profile's preference and what the profile chose for the series.
    private void KeepSubtitlePreference(WatchDetail? detail)
    {
        if (detail is null)
        {
            return;
        }
        var preference = new DownloadSubtitlePreference(PreferredLanguage(detail), detail.SubtitleMode, detail.ShowForcedSubtitles,
            detail.EffectiveSubtitleTrackSignature);
        if (preference != _download.Record.SubtitlePreference)
        {
            _records = KeepAsync(_records, r => r with { SubtitlePreference = preference });
        }
    }

    // The track here in the picked one's language, with its forced and hearing-impaired marks; of several, one from the same
    // place (a file beside the media, or a track inside it) first.
    private LocalSubtitle? LikeHere(SubtitleCandidate picked)
    {
        var alike = _subtitles.Where(s => SameLanguage(s.Item.Language, picked.Language) && s.Item.Forced == picked.Forced
            && (s.Item.HearingImpaired || SubtitleAutoSelection.LabelSaysHearingImpaired(s.Item.Label)) == picked.IsHearingImpaired).ToList();
        return alike.FirstOrDefault(s => s.Item.Source == (picked.Source ?? SubtitleSources.Embedded)) ?? alike.FirstOrDefault();
    }

    // Shows the track chosen now: one of the file's own at once, a file beside it once the player has read it.
    private void ShowSubtitle()
    {
        var track = _subtitle is { } id ? _subtitles.FirstOrDefault(s => s.Item.TrackId == id) : null;
        if (track is null)
        {
            _engine.SetSubtitleStream(0);
            return;
        }
        if (track.Stream is { } stream)
        {
            _engine.SetSubtitleStream(stream);
            return;
        }
        _ = ShowSidecarAsync(track, _loadId);
    }

    private async Task ShowSidecarAsync(LocalSubtitle track, long loadId)
    {
        string id = track.Item.TrackId;
        int? stream;
        try
        {
            if (!_readSidecars.TryGetValue(id, out var read))
            {
                _readSidecars[id] = read = _engine.AddSubtitleAsync(new Uri(track.Path!), loadId);
            }
            stream = await read;
        }
        catch (Exception ex)
        {
            // Nobody waits for it: the title plays on without that subtitle.
            _logger.LogError("Reading a subtitle of download {DownloadId} failed unexpectedly ({Failure})", _download.Record.DownloadId, DownloadLog.Failure(ex));
            stream = null;
        }
        _dispatcher.Post(() =>
        {
            // Another pick, a stop or the title's end came meanwhile.
            if (_stopping || loadId != _loadId || _subtitle != id)
            {
                return;
            }
            if (stream is { } added && _engine.SetSubtitleStream(added))
            {
                return;
            }
            _logger.LogWarning("A subtitle of download {DownloadId} could not be shown; none shows", _download.Record.DownloadId);
            _readSidecars.Remove(id);
            _subtitle = null;
            _engine.SetSubtitleStream(0);
            TracksChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>A subtitle of the download: a file beside the media (<paramref name="Path"/>) or a track of the file (<paramref name="Stream"/>).</summary>
    private sealed record LocalSubtitle(SubtitleInventoryItem Item, string? Path, int? Stream);
}
