namespace Granary.Core.Playback;

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

    /// <summary>A local path or URL played without the server (developer shortcut <c>--play</c>).</summary>
    public string? DirectSource { get; private init; }

    /// <summary>The Watch Party plays this title: the room decides play, pause and position.</summary>
    public bool IsWatchParty { get; private init; }

    /// <summary>The one file a Watch Party plays for everyone; kept through every replan. Null lets the client choose.</summary>
    public string? PinnedFileId { get; private init; }

    /// <summary>The room's playback epoch this request starts; a new one is a new title for the player.</summary>
    public long SelectionRevision { get; private init; }

    /// <summary>Plays <paramref name="contentId"/> from where the profile left off, or from the start.</summary>
    public static PlayRequest Resume(string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest { ContentId = contentId };
    }

    public static PlayRequest FromStart(string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return new PlayRequest { ContentId = contentId, FromBeginning = true };
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
