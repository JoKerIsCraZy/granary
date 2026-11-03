namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// A row of the detail page's Version picker (web: VersionDropdown): the quality, its dynamic range as a badge, and the
/// size and source under it.
/// </summary>
/// <param name="Detail">"38.9 GB · Remux"; the edition first when the title's files have more than one.</param>
public sealed record DetailVersionOption(string FileId, string Summary, string RangeLabel, string Detail, bool IsSelected)
{
    /// <summary>For screen readers: the row as one sentence.</summary>
    public string AccessibleName => string.Join(", ", new[] { Summary, Detail }.Where(part => part.Length > 0));
}

/// <summary>
/// A row of the Audio picker (web: AudioTracksPopover): Auto first, with the track playback picks by itself, then the
/// file's tracks with chips for codec, channels and the file's default.
/// </summary>
/// <param name="Ordinal">The track's place in the file's audio tracks; null for Auto.</param>
public sealed record DetailAudioOption(int? Ordinal, string Title, string Description, string Codec, string Channels, bool IsDefault, bool IsSelected)
{
    public bool IsAuto => Ordinal is null;

    public string AccessibleName => string.Join(", ", new[] { Title, $"{Codec} {Channels}".Trim(), IsDefault ? "default" : "", Description }
        .Where(part => part.Length > 0));
}

public enum DetailSubtitleKind
{
    Auto,
    Off,
    Track,
}

/// <summary>
/// A row of the Subs picker (web: SubtitlesPopover): Auto and Off, then the file's embedded and external tracks under
/// their headings, each with its format and its Forced, HI and Default chips.
/// </summary>
/// <param name="SelectionIndex">A track's combined ordinal (external tracks first), what the start request takes; -1 otherwise.</param>
/// <param name="Section">"Embedded" or "External" on the first row of a section, its heading; empty on the others.</param>
public sealed record DetailSubtitleOption(
    DetailSubtitleKind Kind,
    int SelectionIndex,
    string Title,
    string Description,
    string Format,
    bool IsForced,
    bool IsHearingImpaired,
    bool IsDefault,
    bool IsSelected,
    string Section = "")
{
    public bool HasSection => Section.Length > 0;

    public string AccessibleName => string.Join(", ", new[]
    {
        Title, Format, IsForced ? "forced" : "", IsHearingImpaired ? "for the hearing impaired" : "", IsDefault ? "default" : "", Description,
        Section.Length > 0 ? Section : "",
    }.Where(part => part.Length > 0));
}
