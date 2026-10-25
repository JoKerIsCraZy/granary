using System.Text.RegularExpressions;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>One subtitle track as the automatic choice weighs it.</summary>
/// <param name="SelectionIndex">The combined ordinal (external tracks first) the start request and the inventory use.</param>
/// <param name="Source">An inventory source (<see cref="SubtitleSources"/>); null ranks as embedded.</param>
/// <param name="NeedsBurnIn">Whether showing it makes the server burn it into the picture; null: every bitmap track does.</param>
/// <param name="Label">What a saved signature names the track by; the title when there is none.</param>
public sealed record SubtitleCandidate(
    int SelectionIndex,
    string? Language,
    string? Codec,
    string? Title,
    bool Forced,
    bool HearingImpaired,
    string? Source,
    bool? NeedsBurnIn = null,
    string? Label = null)
{
    public bool IsHearingImpaired => HearingImpaired || SubtitleAutoSelection.LabelSaysHearingImpaired(Title);

    internal string? SignatureLabel => Label ?? Title;

    internal bool RequiresBurnIn => NeedsBurnIn ?? SubtitleAutoSelection.IsBitmap(Codec);

    // Embedded, external, downloaded: external sidecars are the ones that drift out of sync.
    internal int SourceRank => Source?.Trim().ToLowerInvariant() switch
    {
        SubtitleSources.External => 1,
        SubtitleSources.Downloaded => 2,
        _ => 0,
    };
}

/// <summary>
/// The subtitle a playback starts with (docs/api-contract.md, 8.8.1): the start applies no saved subtitle preference,
/// so the client resolves it from the watch detail's effective fields. Android's AutoSubtitleResolver, which Apple's
/// PlaybackPrefsResolver and the web player mirror.
/// </summary>
public static partial class SubtitleAutoSelection
{
    /// <summary>The track to start <paramref name="version"/> with, as a combined ordinal; null for none.</summary>
    public static int? ForStart(WatchDetail detail, WatchFileVersion version)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(version);
        // The audio that will play: the index the start sends, else what the server plays without one, the file's default.
        var audioTrack = version.EffectiveAudioTrackIndex is { } audio && audio >= 0 && audio < version.AudioTracks.Count
            ? version.AudioTracks[audio]
            : version.AudioTracks.FirstOrDefault(t => t.Default) ?? version.AudioTracks.FirstOrDefault();
        string? audioLanguage = audioTrack?.Language ?? version.EffectiveAudioLanguage;
        return Resolve(FromCatalog(version.SubtitleTracks), new SubtitlePreference(
            string.IsNullOrWhiteSpace(detail.EffectiveSubtitleLanguage) ? null : detail.EffectiveSubtitleLanguage,
            detail.SubtitleMode, detail.ShowForcedSubtitles, audioLanguage, detail.EffectiveSubtitleTrackSignature))?.SelectionIndex;
    }

    /// <summary>
    /// A file's watch-detail tracks in combined selection space: external tracks count from 0 in list order, embedded
    /// ones follow after every external (the list's own <c>index</c> is an FFmpeg stream index, never a selection).
    /// Text and an embedded PGS track reach the player as they are; other bitmaps are burned in.
    /// </summary>
    public static IReadOnlyList<SubtitleCandidate> FromCatalog(IReadOnlyList<WatchSubtitleTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int externals = tracks.Count(t => t.External);
        int externalSeen = 0, embeddedSeen = 0;
        return [.. tracks.Select(track => new SubtitleCandidate(
            track.External ? externalSeen++ : externals + embeddedSeen++,
            track.Language,
            track.Codec,
            track.Title,
            track.Forced,
            track.HearingImpaired,
            track.External ? SubtitleSources.External : SubtitleSources.Embedded,
            NeedsBurnIn: IsBitmap(track.Codec) && (track.External || CodecFamily(track.Codec) != "pgs"),
            Label: InventoryLabel(track)))];
    }

    // The label the server lists the track under, which a saved signature names it by: its title, else the embedded
    // title, else for an external file its name, else the language (subtitle_inventory_v3.go).
    private static string? InventoryLabel(WatchSubtitleTrack track)
    {
        string?[] names = track.External
            ? [track.Title, track.EmbeddedTitle, track.FileName is { } file ? Path.GetFileName(file) : null, track.Language]
            : [track.Title, track.EmbeddedTitle, track.Language];
        return names.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))?.Trim();
    }

    /// <summary>
    /// The cascade: mode <c>off</c> shows only the forced track in the audio's language, and that only with forced
    /// subtitles on; then the track a saved signature names exactly; then, without a preferred language, only mode
    /// <c>always</c> picks anything; audio already in the preferred language under <c>auto</c> shows its forced track
    /// (with forced subtitles on) or nothing; otherwise the best track in the language, else any forced track.
    /// </summary>
    public static SubtitleCandidate? Resolve(IReadOnlyList<SubtitleCandidate> candidates, SubtitlePreference preference)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(preference);
        if (candidates.Count == 0)
        {
            return null;
        }
        string mode = string.IsNullOrWhiteSpace(preference.Mode) ? SubtitlePreferenceModes.Auto : preference.Mode.Trim().ToLowerInvariant();
        string? audioLanguage = TrackLanguages.Key(preference.AudioLanguage);
        if (mode == SubtitlePreferenceModes.Off)
        {
            return preference.ShowForced && audioLanguage is not null ? BestForced(candidates, audioLanguage) : null;
        }
        var signature = preference.Signature;
        if (signature is not null && candidates.FirstOrDefault(c => Matches(c, signature)) is { } remembered)
        {
            return remembered;
        }
        if (preference.Language is { } language && string.IsNullOrWhiteSpace(language))
        {
            // An explicitly empty language: no subtitles.
            return null;
        }
        string? target = TrackLanguages.Key(preference.Language);
        if (target is null)
        {
            return mode == SubtitlePreferenceModes.Always ? Best(candidates, null, signature) : null;
        }
        if (mode == SubtitlePreferenceModes.Auto && audioLanguage == target)
        {
            return preference.ShowForced ? BestForced(candidates, target) : null;
        }
        return Best(candidates, target, signature)
            ?? (preference.ShowForced ? First(candidates.Where(c => c.Forced), Preference) : null);
    }

    /// <summary>SDH in the label: "closed caption", "hearing impaired", or CC or SDH as a word.</summary>
    public static bool LabelSaysHearingImpaired(string? label)
    {
        string value = label?.ToLowerInvariant() ?? "";
        return value.Contains("closed caption", StringComparison.Ordinal) || value.Contains("hearing impaired", StringComparison.Ordinal)
            || value.Contains("hearing-impaired", StringComparison.Ordinal) || HearingImpairedToken().IsMatch(value);
    }

    /// <summary>PGS, VobSub and DVB: pictures, which a player either draws itself or gets burned into the video.</summary>
    public static bool IsBitmap(string? codec) => CodecFamily(codec) is "pgs" or "vobsub" or "dvbsub";

    // Raw catalog names and decoder aliases as one family: hdmv_pgs_subtitle is pgs, dvd_subtitle is vobsub.
    internal static string? CodecFamily(string? codec)
    {
        string value = new((codec ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return value switch
        {
            "" => null,
            _ when value.Contains("pgs", StringComparison.Ordinal) => "pgs",
            _ when value.Contains("vobsub", StringComparison.Ordinal) || value.Contains("dvdsubtitle", StringComparison.Ordinal) => "vobsub",
            _ when value.Contains("dvbsub", StringComparison.Ordinal) => "dvbsub",
            _ when value.Contains("subrip", StringComparison.Ordinal) || value.EndsWith("srt", StringComparison.Ordinal) => "subrip",
            _ when value.Contains("webvtt", StringComparison.Ordinal) || value.EndsWith("vtt", StringComparison.Ordinal) => "webvtt",
            _ when value.Contains("tx3g", StringComparison.Ordinal) || value.Contains("movtext", StringComparison.Ordinal) => "tx3g",
            _ when value.Contains("ssa", StringComparison.Ordinal) || value == "ass" => "ssa",
            _ => value,
        };
    }

    // Between tracks in the wanted language, each step only breaking ties in the one before: what this device shows
    // itself before what the server burns in, full dialogue before forced, plain before SDH, embedded before external
    // before downloaded. Full ties keep the list's order.
    private static readonly Comparison<SubtitleCandidate> Preference = (a, b) =>
    {
        int order = a.RequiresBurnIn.CompareTo(b.RequiresBurnIn);
        order = order != 0 ? order : a.Forced.CompareTo(b.Forced);
        order = order != 0 ? order : a.IsHearingImpaired.CompareTo(b.IsHearingImpaired);
        return order != 0 ? order : a.SourceRank.CompareTo(b.SourceRank);
    };

    private static SubtitleCandidate? Best(IReadOnlyList<SubtitleCandidate> candidates, string? language, WatchSubtitleSignature? signature)
    {
        var pool = language is null ? candidates : candidates.Where(c => TrackLanguages.Key(c.Language) == language);
        if (signature is null)
        {
            return First(pool, Preference);
        }
        // The remembered track's traits rank first, so a series watched with SDH keeps SDH where its exact track is missing.
        return First(pool, (a, b) =>
        {
            int closer = Score(b, signature).CompareTo(Score(a, signature));
            return closer != 0 ? closer : Preference(a, b);
        });
    }

    private static SubtitleCandidate? BestForced(IReadOnlyList<SubtitleCandidate> candidates, string language) =>
        First(candidates.Where(c => c.Forced && TrackLanguages.Key(c.Language) == language), Preference);

    // The least by the comparison; the first of equals wins.
    private static SubtitleCandidate? First(IEnumerable<SubtitleCandidate> candidates, Comparison<SubtitleCandidate> comparison)
    {
        SubtitleCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || comparison(candidate, best) < 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    // The very track a saved signature describes: same source, language, codec, label (when it has one), forced and SDH.
    private static bool Matches(SubtitleCandidate candidate, WatchSubtitleSignature signature) =>
        Same(signature.Source, candidate.Source) && SameLanguage(signature.Language, candidate.Language) && Same(signature.Codec, candidate.Codec)
        && (Norm(signature.Label).Length == 0 || Same(signature.Label, candidate.SignatureLabel))
        && signature.Forced == candidate.Forced && signature.HearingImpaired == candidate.IsHearingImpaired;

    private static int Score(SubtitleCandidate candidate, WatchSubtitleSignature signature) =>
        (Same(signature.Source, candidate.Source) ? 4 : 0)
        + (signature.Forced == candidate.Forced ? 2 : 0)
        + (signature.HearingImpaired == candidate.IsHearingImpaired ? 2 : 0)
        + (Same(signature.Codec, candidate.Codec) ? 1 : 0)
        + (Same(signature.Label, candidate.SignatureLabel) ? 1 : 0);

    private static bool SameLanguage(string? a, string? b) =>
        Same(a, b) || (TrackLanguages.Key(a) is { } key && key == TrackLanguages.Key(b));

    private static bool Same(string? a, string? b) => Norm(a) == Norm(b);

    private static string Norm(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    [GeneratedRegex("(^|[^a-z0-9])(cc|sdh)([^a-z0-9]|$)")]
    private static partial Regex HearingImpairedToken();
}

/// <summary>What the automatic choice starts from: the watch detail's effective subtitle fields and the audio that plays.</summary>
/// <param name="Language">The preferred subtitle language; null for no preference, empty for no subtitles.</param>
/// <param name="Mode"><see cref="SubtitlePreferenceModes"/>; blank reads as <c>auto</c>.</param>
public sealed record SubtitlePreference(string? Language, string? Mode, bool ShowForced, string? AudioLanguage, WatchSubtitleSignature? Signature = null);
