using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// The skip pills over the picture ("Skip Intro", "Watch Intro", "Skip Credits") and the marked parts on the seek bar
// (docs/api-contract.md, 11).
public sealed partial class PlayerViewModel
{
    private MarkerPrompts _markerPrompts = new();

    /// <summary>The marked parts of the playing file, in source seconds, for the seek bar.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlaybackMarker> Markers { get; private set; } = [];

    /// <summary>Seek-bar previews of the playing file; null without.</summary>
    [ObservableProperty]
    public partial PlayerTrickplay? Trickplay { get; private set; }

    /// <summary>A skip pill shows; its texts stay while it fades out.</summary>
    [ObservableProperty]
    public partial bool HasSkipPrompt { get; private set; }

    [ObservableProperty]
    public partial string SkipLabel { get; private set; } = "";

    /// <summary>The confirmation above the pill ("Intro skipped"); empty for none.</summary>
    [ObservableProperty]
    public partial string SkipCaption { get; private set; } = "";

    /// <summary>The line above the confirmation: how confident the marker's source is and where it came from ("20% · Detected"); empty for none.</summary>
    [ObservableProperty]
    public partial string SkipProvenance { get; private set; } = "";

    /// <summary>How far an intro pill's 5 s have run, 0 to 1; 0 for a pill that stays.</summary>
    [ObservableProperty]
    public partial double SkipProgress { get; private set; }

    /// <summary>
    /// The pill is "Skip Intro" back with the controls after its time ran out (Silo's design for intro skipping, the
    /// untimed <c>ask</c> offer): it takes no focus as it comes, gives up the focus the timed pill had, and answers OK, Space
    /// or a pad's A only while it has focus; Esc and Back are not its.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSkipPromptWithControls { get; private set; }

    /// <summary>The player's controls show; the page sets it. A Skip Intro whose time ran out comes and goes with them.</summary>
    [ObservableProperty]
    public partial bool AreControlsShown { get; set; }

    /// <summary>
    /// Whether Space presses the pill rather than playing or pausing: only the Skip Intro back with the controls, and only
    /// while it has focus, as the web's untimed offer answers Space (silo-server #3511). Over every other pill and focused
    /// button of the full player Space plays and pauses.
    /// </summary>
    public bool SpacePressesSkipPrompt(bool hasFocus) => hasFocus && HasSkipPrompt && IsSkipPromptWithControls;

    /// <summary>The viewer turned the intro pill away (Esc): false when none shows, so Esc does what it does otherwise.</summary>
    public bool DismissSkipPrompt()
    {
        if (!HasSkipPrompt || !_markerPrompts.Dismiss())
        {
            return false;
        }
        ShowSkipPrompt();
        return true;
    }

    /// <summary>
    /// The pill: skips the part, or for "Watch Intro" goes back to the intro's start. It is judged by where playback is
    /// now, a seek on its way included, as the pills follow that position: one the viewer has sought away from seeks
    /// nowhere and goes.
    /// </summary>
    [RelayCommand]
    private void Skip()
    {
        if (_markerPrompts.Select(SkipOrigin) is { } target)
        {
            SeekTo(target);
        }
        ShowSkipPrompt();
    }

    // While playback cannot go on the pills stay hidden until it does (FollowMarkers), whatever the controls do.
    partial void OnAreControlsShownChanged(bool value)
    {
        _markerPrompts.FollowControls(value);
        if (FollowsMarkers)
        {
            ShowSkipPrompt();
        }
    }

    // A new playback starts with nothing resolved; the controls stay as they are.
    private void ResetMarkers()
    {
        _markerPrompts = new MarkerPrompts();
        _markerPrompts.FollowControls(AreControlsShown);
        Markers = [];
        Trickplay = null;
        ShowSkipPrompt();
    }

    private void OnTrickplayChanged(object? sender, EventArgs e)
    {
        if (sender == _playback && _playback is { } playback)
        {
            Trickplay = playback.Trickplay;
        }
    }

    private void OnMarkersChanged(object? sender, EventArgs e)
    {
        if (sender == _playback && _playback is { } playback)
        {
            TakeMarkers(playback);
        }
    }

    // Another file, or more markers of this one; a change of tracks alone leaves a pill as it is. A showing pill is
    // judged by where playback is now, so a read during a seek the engine has not reported yet does not keep one it
    // has left.
    private void TakeMarkers(IServerPlayback playback)
    {
        var markers = playback.Markers;
        if (markers.SequenceEqual(Markers))
        {
            return;
        }
        Markers = markers;
        _markerPrompts.Reset(markers, RoomMarkers(playback.Preferences.Markers), SkipOrigin);
        TakeCredits(markers);
        ShowSkipPrompt();
    }

    // The skip settings changed under known markers (a room's ended): the pills follow the profile's own again.
    private void RetakeMarkerSettings()
    {
        if (_playback is { } playback && Markers.Count > 0)
        {
            _markerPrompts.Reset(Markers, RoomMarkers(playback.Preferences.Markers), SkipOrigin);
            ShowSkipPrompt();
        }
    }

    // The pills follow the position while the title plays and can be sought; a part that skips itself is passed here.
    private void FollowMarkers(PlaybackSnapshot state)
    {
        var now = _time.GetElapsedTime(_created);
        if (!FollowsMarkers)
        {
            // Hidden and its clock stopped until playback goes on.
            _markerPrompts.Suspend(now);
            HasSkipPrompt = false;
            return;
        }
        var activity = state.IsPaused ? MarkerActivity.Paused : IsBuffering ? MarkerActivity.Stalled : MarkerActivity.Playing;
        if (_markerPrompts.Update(SkipOrigin, activity, now) is { } target)
        {
            Seek(target);
        }
        ShowSkipPrompt();
    }

    private bool FollowsMarkers => _playback is not null && CanSeek && Phase == PlayerPhase.Playing;

    // The texts first: whoever sees the pill appear reads them. The card and the screen of the next episode take its place,
    // and a room's guest, who cannot seek, has none.
    private void ShowSkipPrompt()
    {
        if (_markerPrompts.Prompt is not { } prompt || _nextUp.View != NextUpView.None || !CanSeekInRoom)
        {
            HasSkipPrompt = false;
            // After the pill: the page reads a change of it as a pill that shows becoming another.
            IsSkipPromptWithControls = false;
            return;
        }
        SkipLabel = prompt.Label;
        SkipCaption = prompt.Caption ?? "";
        SkipProvenance = prompt.Provenance ?? "";
        SkipProgress = prompt.Progress ?? 0;
        IsSkipPromptWithControls = prompt.WithControls;
        HasSkipPrompt = true;
    }
}
