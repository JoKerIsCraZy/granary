using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels;

// The web player's Playback info (PlaybackInfoOverlay), read again every second while it shows, and its Fill screen.
public sealed partial class PlayerViewModel
{
    private static readonly TimeSpan PlaybackInfoRefresh = TimeSpan.FromSeconds(1);

    private ITimer? _playbackInfoTimer;
    private (int Width, int Height) _pictureSize;

    [ObservableProperty]
    public partial bool IsPlaybackInfoShown { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<PlaybackInfoSection> PlaybackInfoSections { get; private set; } = [];

    /// <summary>The picture fills the page, cut where it does not fit, instead of showing whole with bars.</summary>
    [ObservableProperty]
    public partial bool IsFillingScreen { get; private set; }

    /// <summary>The picture's area in pixels, as the page lays it out; the playback info shows it.</summary>
    public void SetPictureSize(int width, int height) => _pictureSize = (width, height);

    [RelayCommand]
    private void TogglePlaybackInfo()
    {
        IsPlaybackInfoShown = !IsPlaybackInfoShown;
        if (!IsPlaybackInfoShown)
        {
            StopPlaybackInfo();
            return;
        }
        ShowPlaybackInfo(_engine.ReadStats());
        // Read where the timer fires: mpv answers on its own lock, not on the UI thread.
        _playbackInfoTimer ??= _time.CreateTimer(_ =>
        {
            var stats = _engine.ReadStats();
            _dispatcher.Post(() => ShowPlaybackInfo(stats));
        }, null, PlaybackInfoRefresh, PlaybackInfoRefresh);
    }

    [RelayCommand]
    private void ToggleFill()
    {
        IsFillingScreen = !IsFillingScreen;
        _engine.SetFill(IsFillingScreen);
    }

    private void ShowPlaybackInfo(PlaybackStats stats)
    {
        if (!IsPlaybackInfoShown)
        {
            return;
        }
        var sections = PlaybackInfo.Sections(_playback?.StreamInfo, stats, _pictureSize.Width, _pictureSize.Height);
        // The same values keep their rows, so a scrolled panel stays where it is.
        if (sections.Count != PlaybackInfoSections.Count
            || sections.Zip(PlaybackInfoSections).Any(pair => pair.First.Title != pair.Second.Title || !pair.First.Rows.SequenceEqual(pair.Second.Rows)))
        {
            PlaybackInfoSections = sections;
        }
    }

    private void StopPlaybackInfo()
    {
        _playbackInfoTimer?.Dispose();
        _playbackInfoTimer = null;
    }

    // A new title shows its whole picture again, as the web player does for a new session.
    private void ResetFill()
    {
        IsFillingScreen = false;
        _engine.SetFill(false);
    }
}
