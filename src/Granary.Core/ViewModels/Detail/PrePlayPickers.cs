using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels.Detail;

/// <summary>How the viewer set the Subs picker.</summary>
public enum SubtitlePickMode
{
    Auto,
    Off,
    Track,
}

/// <summary>What the viewer picked on the detail page; null members and Auto leave the choice to playback.</summary>
/// <param name="SubtitleTrack">With <see cref="SubtitlePickMode.Track"/>: the track's combined ordinal.</param>
/// <param name="SubtitleOverrideCleared">
/// Auto was picked over a remembered track: the item's saved signature no longer applies, although the item may still name
/// it until it is read again.
/// </param>
/// <param name="AudioAutoPending">Auto was picked for audio, and what the server picks without the saved track is not read yet.</param>
/// <param name="SubtitleAutoPending">The same for subtitles.</param>
public sealed record PrePlayPicks(
    string? FileId = null,
    int? AudioTrack = null,
    SubtitlePickMode SubtitleMode = SubtitlePickMode.Auto,
    int? SubtitleTrack = null,
    bool SubtitleOverrideCleared = false,
    bool AudioAutoPending = false,
    bool SubtitleAutoPending = false)
{
    public static PrePlayPicks None { get; } = new();
}

/// <summary>
/// The detail page's Version, Audio and Subs pickers for a movie or episode, as Silo's web detail page lays them out under
/// its buttons (web: ActionBar.tsx with VersionDropdown, AudioTracksPopover, SubtitlesPopover), and what Play then passes
/// on. Until the viewer picks, each shows what playback picks by itself: the file <see cref="PlaybackStarter.ChooseVersion(WatchDetail)"/>
/// takes, the audio track the profile's preferences pick, the subtitle <see cref="SubtitleAutoSelection"/> resolves for
/// that audio. A series or season has no pickers: it plays an episode.
/// </summary>
public sealed class PrePlayPickers
{
    private static readonly StringComparer TextOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    private readonly IReadOnlyList<SubtitleCandidate> _candidates;

    private PrePlayPickers(
        FileVersion? version,
        IReadOnlyList<DetailVersionOption> versions,
        IReadOnlyList<DetailAudioOption> audio,
        string audioSummary,
        IReadOnlyList<DetailSubtitleOption> subtitles,
        string subtitleSummary,
        IReadOnlyList<SubtitleCandidate> candidates,
        PrePlayChoice? choice)
    {
        Version = version;
        Versions = versions;
        VersionSummary = version is null ? "" : PrePlayText.VersionSummary(version);
        Audio = audio;
        AudioSummary = audioSummary;
        Subtitles = subtitles;
        SubtitleSummary = subtitleSummary;
        _candidates = candidates;
        Choice = choice;
    }

    public static PrePlayPickers None { get; } = new(null, [], [], "", [], "", [], null);

    /// <summary>The file Play plays: the viewer's pick, else the one playback would choose.</summary>
    public FileVersion? Version { get; }

    /// <summary>The title's files, the highest resolution first.</summary>
    public IReadOnlyList<DetailVersionOption> Versions { get; }

    /// <summary>Only a title with more than one file has a Version picker.</summary>
    public bool ShowsVersions => Versions.Count > 1;

    /// <summary>"2160p · HEVC · HDR10 · DTS".</summary>
    public string VersionSummary { get; }

    public string VersionName => "Version, " + VersionSummary;

    /// <summary>Auto, then the file's tracks; none when the file lists no audio.</summary>
    public IReadOnlyList<DetailAudioOption> Audio { get; }

    public bool ShowsAudio => Audio.Count > 0;

    /// <summary>"Auto: German · TrueHD Atmos · 7.1", or the picked track's words.</summary>
    public string AudioSummary { get; }

    public string AudioName => "Audio, " + AudioSummary;

    /// <summary>Auto, Off, then the file's tracks; none when the file has no subtitles.</summary>
    public IReadOnlyList<DetailSubtitleOption> Subtitles { get; }

    public bool ShowsSubtitles => Subtitles.Count > 0;

    /// <summary>"Auto: English · SRT", "Off", or the picked track's words.</summary>
    public string SubtitleSummary { get; }

    public string SubtitleName => "Subtitles, " + SubtitleSummary;

    public bool ShowsAny => ShowsVersions || ShowsAudio || ShowsSubtitles;

    /// <summary>What Play passes on; null without a pick, so playback chooses as from anywhere else.</summary>
    public PrePlayChoice? Choice { get; }

    /// <summary>The pickers of <paramref name="item"/>'s files with <paramref name="picks"/> applied; <see cref="None"/> without files.</summary>
    public static PrePlayPickers For(CatalogItemDetail item, PrePlayPicks picks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(picks);
        var files = PrePlayText.SortByResolution(item.Versions.Where(v => v.FileId.Length > 0));
        var picked = files.FirstOrDefault(v => v.FileId == picks.FileId);
        if ((picked ?? PlaybackStarter.ChooseVersion(item.Versions, v => v.FileId, v => v.PresentationPartIndex, item.UserData?.LastFileId)) is not { } version)
        {
            return None;
        }
        var audioTracks = version.AudioTracks;
        int? audio = picks.AudioTrack is { } ordinal && ordinal >= 0 && ordinal < audioTracks.Count ? ordinal : null;
        var candidates = SubtitleAutoSelection.FromCatalog(version.SubtitleTracks);
        int? subtitle = picks.SubtitleMode switch
        {
            SubtitlePickMode.Off => -1,
            SubtitlePickMode.Track when candidates.Any(c => c.SelectionIndex == picks.SubtitleTrack) => picks.SubtitleTrack,
            _ => null,
        };
        var subtitleMode = subtitle switch
        {
            null => SubtitlePickMode.Auto,
            -1 => SubtitlePickMode.Off,
            _ => SubtitlePickMode.Track,
        };
        var preference = new SubtitlePreference(
            string.IsNullOrWhiteSpace(item.EffectiveSubtitleLanguage) ? null : item.EffectiveSubtitleLanguage,
            item.SubtitleMode,
            item.ShowForcedSubtitles,
            SubtitleAutoSelection.AudioLanguage(audioTracks, audio ?? version.EffectiveAudioTrackIndex, version.EffectiveAudioLanguage),
            picks.SubtitleOverrideCleared ? null : item.EffectiveSubtitleTrackSignature);
        var auto = SubtitleAutoSelection.Resolve(candidates, preference);
        // Until the server is read again after Auto, what it picks is not known: Auto alone, the picks' own state.
        bool audioPending = audio is null && picks.AudioAutoPending;
        bool subtitlePending = subtitleMode == SubtitlePickMode.Auto && picks.SubtitleAutoPending;
        var (subtitleOptions, subtitleSummary) = SubtitleRows(version, candidates, auto, preference.Signature is not null, subtitleMode, subtitle,
            subtitlePending);
        // Auto over a remembered track the item still names (its forgetting on the way, or done but not read back): a start
        // would apply that track, so it is told the automatic one (or none) instead, which is what the viewer chose. A
        // forgetting the server refused puts the picker back on the remembered track, and this no longer applies.
        int? startSubtitle = subtitle ?? (subtitleMode == SubtitlePickMode.Auto && picks.SubtitleOverrideCleared
            && item.EffectiveSubtitleTrackSignature is not null && candidates.Count > 0 ? auto?.SelectionIndex ?? -1 : null);
        var choice = picked is not null || audio is not null || startSubtitle is not null
            ? new PrePlayChoice(version.FileId, picked is not null, audio, startSubtitle)
            : null;
        return new PrePlayPickers(version, VersionRows(files, version), AudioRows(version, audio, audioPending), AudioSummaryOf(version, audio, audioPending),
            subtitleOptions, subtitleSummary, candidates, choice);
    }

    /// <summary>What a pick of audio track <paramref name="ordinal"/> saves for the title (docs/api-contract.md, 8.8); null for none.</summary>
    public AudioPreference? AudioPreferenceFor(int ordinal) =>
        Version is { } version && ordinal >= 0 && ordinal < version.AudioTracks.Count ? AudioPreference.Of(version.AudioTracks[ordinal], ordinal) : null;

    /// <summary>
    /// What a pick of the track with <paramref name="selectionIndex"/> saves for the title: its catalog codec and label, as
    /// the web's detail page and Apple's client save them; null for none.
    /// </summary>
    public SubtitleChoice? SubtitleChoiceFor(int selectionIndex) =>
        _candidates.FirstOrDefault(c => c.SelectionIndex == selectionIndex) is { } track
            ? SubtitleChoice.Of(track.SelectionIndex, new SubtitleTrackSignature
            {
                Source = track.Source,
                Language = track.Language,
                Codec = track.Codec,
                Label = track.SignatureLabel,
                Forced = track.Forced,
                HearingImpaired = track.IsHearingImpaired,
            })
            : null;

    // Each file once, the edition first in its line when the files differ in it: the web picks the edition in a picker of
    // its own, which Granary leaves out.
    private static IReadOnlyList<DetailVersionOption> VersionRows(IReadOnlyList<FileVersion> files, FileVersion selected)
    {
        bool editions = files.Select(v => v.EditionRaw?.Trim() ?? "").Distinct(StringComparer.Ordinal).Count() > 1;
        return [.. files.Select(v => new DetailVersionOption(
            v.FileId,
            PrePlayText.QualitySummary(v) is { Length: > 0 } summary ? summary : "Version " + v.FileId,
            PlaybackInfo.VideoRangeLabel(v.VideoTracks, v.Hdr),
            string.Join(" · ", new[] { editions ? v.EditionRaw?.Trim() : null, PrePlayText.DetailLine(v) }.Where(p => !string.IsNullOrEmpty(p))),
            v.FileId == selected.FileId))];
    }

    // web: getAutoAudioTrackIndex, the track the profile's preferences pick, else the file's default, else the first.
    private static int AutoAudio(FileVersion version)
    {
        var tracks = version.AudioTracks;
        if (version.EffectiveAudioTrackIndex is { } index && index >= 0 && index < tracks.Count)
        {
            return index;
        }
        int marked = tracks.ToList().FindIndex(t => t.Default);
        return Math.Max(0, marked);
    }

    private static string AudioSummaryOf(FileVersion version, int? picked, bool pending)
    {
        if (version.AudioTracks.Count == 0)
        {
            return "";
        }
        return picked is { } ordinal ? PrePlayText.AudioSummary(version.AudioTracks[ordinal])
            : pending ? "Auto"
            : "Auto: " + PrePlayText.AudioSummary(version.AudioTracks[AutoAudio(version)]);
    }

    private static IReadOnlyList<DetailAudioOption> AudioRows(FileVersion version, int? picked, bool pending)
    {
        var tracks = version.AudioTracks;
        if (tracks.Count == 0)
        {
            return [];
        }
        var auto = new DetailAudioOption(null, "Auto", pending ? "" : PrePlayText.AudioSummary(tracks[AutoAudio(version)]), "", "", false,
            picked is null);
        return
        [
            auto,
            .. tracks.Select((track, ordinal) =>
            {
                string title = TrackText.SubtitleLanguage(track.Language);
                string embedded = track.Title?.Trim() is { Length: > 0 } own ? own : track.EmbeddedTitle?.Trim() ?? "";
                string description = string.Join(" · ", new[] { embedded != title ? embedded : "", TrackText.CompactAudioMeta(track) }
                    .Where(p => p.Length > 0));
                return new DetailAudioOption(ordinal, title, description, TrackText.AudioCodec(track), TrackText.Channels(track.Channels),
                    track.Default, ordinal == picked);
            }),
        ];
    }

    // web: buildVersionSubtitleInventory and buildPrePlaySubtitleCandidates without downloaded subtitles, which Silo's
    // apps leave out before playback as well: each source by language, forced first, the file's default first.
    // auto: the automatic choice; remembers: it is the track the item remembers. pending: Auto was picked and what the
    // server then picks is not read yet.
    private static (IReadOnlyList<DetailSubtitleOption> Rows, string Summary) SubtitleRows(FileVersion version,
        IReadOnlyList<SubtitleCandidate> candidates, SubtitleCandidate? auto, bool remembers, SubtitlePickMode mode, int? picked, bool pending)
    {
        var tracks = version.SubtitleTracks;
        if (tracks.Count == 0)
        {
            return ([], "");
        }
        var rows = tracks.Select((track, i) => new SubtitleRow(track, candidates[i])).ToList();
        var autoRow = rows.FirstOrDefault(r => ReferenceEquals(r.Candidate, auto));
        // A remembered track stands as the choice itself, not as an Auto guess (web: overrideCandidate).
        var remembered = mode == SubtitlePickMode.Auto && remembers ? autoRow : null;
        string summary = mode switch
        {
            SubtitlePickMode.Off => "Off",
            SubtitlePickMode.Track => rows.First(r => r.Candidate.SelectionIndex == picked).Summary,
            _ when pending => "Auto",
            _ when remembered is not null => remembered.Summary,
            _ => "Auto: " + (autoRow?.Summary ?? "Off"),
        };
        int? chosen = mode == SubtitlePickMode.Track ? picked : remembered?.Candidate.SelectionIndex;
        string autoDetail = pending ? "" : remembered is not null ? "Reset to profile defaults" : autoRow?.LanguageLabel ?? "Off";
        var options = new List<DetailSubtitleOption>
        {
            new(DetailSubtitleKind.Auto, -1, "Auto", autoDetail, "", false, false, false, mode == SubtitlePickMode.Auto && remembered is null),
            new(DetailSubtitleKind.Off, -1, "Off", "", "", false, false, false, mode == SubtitlePickMode.Off),
        };
        options.AddRange(Section(rows.Where(r => !r.Track.External), "Embedded", chosen));
        options.AddRange(Section(rows.Where(r => r.Track.External), "External", chosen));
        return (options, summary);
    }

    private static IEnumerable<DetailSubtitleOption> Section(IEnumerable<SubtitleRow> rows, string heading, int? chosen) =>
        rows.OrderBy(r => r.LanguageLabel, TextOrder)
            .ThenBy(r => r.Track.Forced ? 0 : 1)
            .ThenBy(r => r.Track.Default ? 0 : 1)
            .ThenBy(r => r.Title ?? r.Track.Codec ?? "", TextOrder)
            .Select((r, i) => r.Option(chosen, i == 0 ? heading : ""));

    private sealed record SubtitleRow(WatchSubtitleTrack Track, SubtitleCandidate Candidate)
    {
        public string LanguageLabel { get; } = TrackText.SubtitleLanguage(Track.Language);

        // The track's own name: its title, the embedded one, else an external file's name without its folders (web:
        // normalizeBuiltInTrack, which the server's own path would otherwise reach).
        public string? Title { get; } = new[] { Track.Title, Track.EmbeddedTitle, TrackText.FileLabel(Track.FileName) }
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))?.Trim();

        public string Summary => PrePlayText.SubtitleSummary(LanguageLabel, Title, Track.Codec, Track.Forced, Track.HearingImpaired);

        public DetailSubtitleOption Option(int? chosen, string section)
        {
            // A title that is only "Forced" or "SDH" is a flag, not a name (web: inferSubtitleFlagsFromTitle).
            string flag = Title?.ToLowerInvariant() ?? "";
            bool forced = flag == "forced";
            bool hearingImpaired = flag is "sdh" or "cc" or "hi" or "hearing impaired";
            string description = forced || hearingImpaired ? "" : TrackText.SubtitleDetail(Title, Track.Language, LanguageLabel, Track.Codec);
            return new DetailSubtitleOption(DetailSubtitleKind.Track, Candidate.SelectionIndex, LanguageLabel, description,
                Track.Codec is { Length: > 0 } ? PrePlayText.SubtitleFormat(Track.Codec) : "", Track.Forced || forced,
                Track.HearingImpaired || hearingImpaired, Track.Default, Candidate.SelectionIndex == chosen, section);
        }
    }
}
