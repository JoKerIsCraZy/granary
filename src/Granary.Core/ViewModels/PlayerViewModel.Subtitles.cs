using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels;

// The web player's subtitle button: there for every server title, tracks or not, since its menu also adds subtitles;
// its glyph and dot say whether one shows. C turns them off and back on to the track that showed last.
public sealed partial class PlayerViewModel
{
    // The track that showed last, for C to bring back; the title's own, so a new title starts without one.
    private string? _lastSubtitle;

    /// <summary>A server title plays: the subtitle button and its menu are there.</summary>
    [ObservableProperty]
    public partial bool HasSubtitleMenu { get; private set; }

    /// <summary>A subtitle shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelayEarlier), nameof(CanDelayLater), nameof(CanResetSubtitleDelay))]
    public partial bool IsSubtitleOn { get; private set; }

    /// <summary>The file that plays, which Add Subtitles adds to; null before a plan.</summary>
    public string? SubtitleFileId => _playback?.PlayingFileId is { Length: > 0 } id ? id : null;

    /// <summary>
    /// The C key: the subtitle that shows goes off, remembered; with none showing, the one remembered comes back. Both
    /// are the viewer's picks, saved for the series.
    /// </summary>
    public void ToggleSubtitles()
    {
        if (_playback is not { } playback)
        {
            return;
        }
        if (playback.SubtitleTrack is { } showing)
        {
            _lastSubtitle = showing;
            SelectSubtitle(null);
        }
        else if (_lastSubtitle is { } last && playback.SubtitleTracks.Any(t => t.TrackId == last))
        {
            SelectSubtitle(last);
        }
    }

    /// <summary>Add Subtitles stored <paramref name="storedSubtitleId"/> for the file: a new plan lists it, and it shows.</summary>
    public void ShowAddedSubtitle(string storedSubtitleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSubtitleId);
        _playback?.ShowAddedSubtitle(storedSubtitleId);
    }

    private void TakeSubtitles(Playback.IServerPlayback playback)
    {
        // Off is a row even without tracks: the menu still offers to add some.
        SubtitleTracks = Player.SubtitleTrackOption.For(playback.SubtitleTracks, playback.SubtitleTrack);
        IsSubtitleOn = playback.SubtitleTrack is not null;
        if (playback.SubtitleTrack is { } showing)
        {
            _lastSubtitle = showing;
        }
    }

    private void ResetSubtitles()
    {
        SubtitleTracks = [];
        IsSubtitleOn = false;
        _lastSubtitle = null;
    }
}
