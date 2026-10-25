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

    /// <summary>A skip pill shows; its texts stay while it fades out.</summary>
    [ObservableProperty]
    public partial bool HasSkipPrompt { get; private set; }

    [ObservableProperty]
    public partial string SkipLabel { get; private set; } = "";

    /// <summary>The confirmation above the pill ("Intro skipped"); empty for none.</summary>
    [ObservableProperty]
    public partial string SkipCaption { get; private set; } = "";

    /// <summary>How far an intro pill's 5 s have run, 0 to 1; 0 for a pill that stays.</summary>
    [ObservableProperty]
    public partial double SkipProgress { get; private set; }

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

    /// <summary>The pill: skips the part, or for "Watch Intro" goes back to the intro's start.</summary>
    [RelayCommand]
    private void Skip()
    {
        if (_markerPrompts.Select() is { } target)
        {
            SeekTo(target);
        }
        ShowSkipPrompt();
    }

    // A new playback starts with nothing resolved.
    private void ResetMarkers()
    {
        _markerPrompts = new MarkerPrompts();
        Markers = [];
        ShowSkipPrompt();
    }

    private void OnMarkersChanged(object? sender, EventArgs e)
    {
        if (sender == _playback && _playback is { } playback)
        {
            TakeMarkers(playback);
        }
    }

    // Another file, or more markers of this one; a change of tracks alone leaves a pill as it is.
    private void TakeMarkers(IServerPlayback playback)
    {
        var markers = playback.Markers;
        if (markers.SequenceEqual(Markers))
        {
            return;
        }
        Markers = markers;
        _markerPrompts.Reset(markers, playback.Preferences.Markers);
        ShowSkipPrompt();
    }

    // The pills follow the position while the title plays and can be sought; a part that skips itself is passed here.
    private void FollowMarkers(PlaybackSnapshot state)
    {
        var now = _time.GetElapsedTime(_created);
        if (_playback is null || !CanSeek || Phase != PlayerPhase.Playing)
        {
            // Hidden and its clock stopped until playback goes on.
            _markerPrompts.Suspend(now);
            HasSkipPrompt = false;
            return;
        }
        var activity = state.IsPaused ? MarkerActivity.Paused : IsBuffering ? MarkerActivity.Stalled : MarkerActivity.Playing;
        if (_markerPrompts.Update(SkipOrigin, activity, now) is { } target)
        {
            SeekTo(target);
        }
        ShowSkipPrompt();
    }

    // The texts first: whoever sees the pill appear reads them.
    private void ShowSkipPrompt()
    {
        if (_markerPrompts.Prompt is not { } prompt)
        {
            HasSkipPrompt = false;
            return;
        }
        SkipLabel = prompt.Label;
        SkipCaption = prompt.Caption ?? "";
        SkipProgress = prompt.Progress ?? 0;
        HasSkipPrompt = true;
    }
}
