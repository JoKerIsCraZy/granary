namespace Granary.Core.Playback;

/// <summary>
/// What the viewer chose on a title's detail page before Play (the web's version, audio and subtitle pickers): the file
/// and its tracks. Track picks are that file's, so they only travel with it; null members leave the choice to playback.
/// </summary>
/// <param name="FileId">The file to play; when the title no longer has it, playback chooses as without a pick.</param>
/// <param name="IsVersionPicked">
/// The viewer picked <paramref name="FileId"/> itself, rather than only its tracks: the server may then not swap in
/// another version (Silo's web, Android and Apple clients pin a chosen version the same way).
/// </param>
/// <param name="AudioTrack">An ordinal into the file's audio tracks.</param>
/// <param name="SubtitleTrack">
/// The combined ordinal the start request takes (external tracks first); -1 for off. Counted from the catalog's listing of
/// the file's tracks, which the watch detail lists alike: the start only checks it is in range.
/// </param>
public sealed record PrePlayChoice(string FileId, bool IsVersionPicked, int? AudioTrack = null, int? SubtitleTrack = null);

/// <summary>What the player plays: the navigation parameter of the player route.</summary>
public sealed record PlayRequest
{
    private PlayRequest()
    {
    }

    /// <summary>The movie or episode to play from the server; null for <see cref="Direct"/>.</summary>
    public string? ContentId { get; private init; }

    /// <summary>Start at 0 instead of the resume point ("Start over").</summary>
    public bool FromBeginning { get; private init; }

    /// <summary>A resume lands on the resume point itself, whatever this PC's Rewind on resume (Android's <c>suppressResumeRewind</c>).</summary>
    public bool SuppressesResumeRewind { get; private init; }

    /// <summary>A local path or URL played without the server (developer shortcut <c>--play</c>).</summary>
    public string? DirectSource { get; private init; }

    /// <summary>The Watch Party plays this title: the room decides play, pause and position.</summary>
    public bool IsWatchParty { get; private init; }

    /// <summary>The one file a Watch Party plays for everyone; kept through every replan. Null lets the client choose.</summary>
    public string? PinnedFileId { get; private init; }

    /// <summary>The room's playback epoch this request starts; a new one is a new title for the player.</summary>
    public long SelectionRevision { get; private init; }

    /// <summary>The file and tracks picked on the detail page; null lets playback choose (every other way in).</summary>
    public PrePlayChoice? Choice { get; private init; }

    /// <summary>Plays <paramref name="contentId"/> from where the profile left off, or from the start.</summary>
    public static PlayRequest Resume(string contentId, PrePlayChoice? choice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest { ContentId = contentId, Choice = choice };
    }

    /// <summary>
    /// The episode before or after the playing one, from where the profile left it but never rewound: Android rewinds no
    /// change of episode (<c>suppressResumeRewind</c>).
    /// </summary>
    public static PlayRequest ResumeWithoutRewind(string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest { ContentId = contentId, SuppressesResumeRewind = true };
    }

    public static PlayRequest FromStart(string contentId, PrePlayChoice? choice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest { ContentId = contentId, FromBeginning = true, Choice = choice };
    }

    /// <summary>
    /// The Watch Party's title: from the start, file <paramref name="fileId"/> for everyone (no other version), while the
    /// room's commands play, pause and seek it.
    /// </summary>
    public static PlayRequest WatchParty(string contentId, string? fileId, long selectionRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest
        {
            ContentId = contentId,
            FromBeginning = true,
            IsWatchParty = true,
            PinnedFileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId,
            SelectionRevision = selectionRevision,
        };
    }

    public static PlayRequest Direct(string pathOrUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrUrl);
        return new PlayRequest { DirectSource = pathOrUrl };
    }
}
