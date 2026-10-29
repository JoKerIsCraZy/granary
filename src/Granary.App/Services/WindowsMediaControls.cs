using Granary.App.Controls;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Storage.Streams;

namespace Granary.App.Services;

/// <summary>
/// Windows' media controls of the main window (<c>SystemMediaTransportControlsInterop.GetForWindow</c>): the media keys and
/// their overlay. Shown and cleared on the UI thread; Windows raises the buttons on a thread of its own.
/// </summary>
internal sealed class WindowsMediaControls(MainWindow window, ILogger<WindowsMediaControls> logger) : ISystemMediaControls
{
    // Failures in a row after which the player goes on without the controls.
    private const int FailuresAllowed = 3;

    private SystemMediaTransportControls? _controls;
    private bool _unavailable;
    private int _failures;
    private SystemMediaState? _shown;
    private CancellationTokenSource? _artwork;
    // The picture Windows reads; kept until another one replaces it.
    private InMemoryRandomAccessStream? _thumbnail;

    public event EventHandler<SystemMediaRequest>? Requested;

    // Called from the player's property changes and from its page's unload: a failure of Windows' controls must neither end
    // the app nor keep the page from letting go of the player.
    public void Show(SystemMediaState state) => Guarded(() => ShowState(state));

    public void Clear() => Guarded(ClearState);

    private void Guarded(Action action)
    {
        if (_unavailable)
        {
            return;
        }
        try
        {
            action();
            _failures = 0;
        }
        catch (Exception ex)
        {
            // A state shown in part is shown again whole next time.
            _shown = null;
            _unavailable = ++_failures >= FailuresAllowed;
            logger.LogWarning("Windows' media controls failed: {Failure}{GivenUp}", ex.GetType().Name, _unavailable ? "; the player goes on without them" : "");
        }
    }

    private void ShowState(SystemMediaState state)
    {
        if (Controls is not { } controls)
        {
            return;
        }
        var shown = _shown;
        _shown = state;
        controls.IsEnabled = true;
        if (shown is null || shown.Title != state.Title || shown.Subtitle != state.Subtitle || shown.Artwork != state.Artwork)
        {
            ShowTitle(controls, state, newPicture: shown?.Artwork != state.Artwork || shown is null);
        }
        controls.PlaybackStatus = state.Status switch
        {
            SystemMediaStatus.Playing => MediaPlaybackStatus.Playing,
            SystemMediaStatus.Paused => MediaPlaybackStatus.Paused,
            SystemMediaStatus.Stopped => MediaPlaybackStatus.Stopped,
            _ => MediaPlaybackStatus.Changing,
        };
        controls.IsPlayEnabled = controls.IsPauseEnabled = state.CanPlayPause;
        controls.IsRewindEnabled = controls.IsFastForwardEnabled = state.CanSeek;
        controls.IsPreviousEnabled = state.CanGoPrevious;
        controls.IsNextEnabled = state.CanGoNext;
        // As in Silo's apps' video players, there is no Stop.
        controls.IsStopEnabled = false;
        if (shown is null || shown.PositionSeconds != state.PositionSeconds || shown.DurationSeconds != state.DurationSeconds || shown.CanSeek != state.CanSeek)
        {
            ShowTimeline(controls, state);
        }
    }

    private void ClearState()
    {
        _shown = null;
        StopArtwork();
        if (_controls is not { } controls)
        {
            return;
        }
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.DisplayUpdater.ClearAll();
        controls.DisplayUpdater.Update();
        controls.IsEnabled = false;
        _thumbnail?.Dispose();
        _thumbnail = null;
    }

    // Made the first time something plays; a Windows without them leaves the player without them.
    private SystemMediaTransportControls? Controls
    {
        get
        {
            if (_controls is null && !_unavailable)
            {
                try
                {
                    _controls = SystemMediaTransportControlsInterop.GetForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
                    _controls.ButtonPressed += OnButtonPressed;
                    _controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;
                }
                catch (Exception ex)
                {
                    _unavailable = true;
                    logger.LogWarning("Windows' media controls are not available: {Failure}", ex.GetType().Name);
                }
            }
            return _controls;
        }
    }

    private void ShowTitle(SystemMediaTransportControls controls, SystemMediaState state, bool newPicture)
    {
        var display = controls.DisplayUpdater;
        display.Type = MediaPlaybackType.Video;
        display.VideoProperties.Title = state.Title;
        display.VideoProperties.Subtitle = state.Subtitle;
        if (newPicture)
        {
            StopArtwork();
            display.Thumbnail = null;
            _thumbnail?.Dispose();
            _thumbnail = null;
            if (state.Artwork is { } url)
            {
                _artwork = new CancellationTokenSource();
                _ = ShowArtworkAsync(controls, url, _artwork.Token);
            }
        }
        display.Update();
    }

    // The picture from the app's artwork cache, as bytes: its presigned address never goes to Windows.
    private async Task ShowArtworkAsync(SystemMediaTransportControls controls, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await Artwork.StreamAsync(url, cancellationToken);
            if (stream is null)
            {
                return;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                stream.Dispose();
                return;
            }
            _thumbnail = stream;
            controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            logger.LogWarning("The picture for Windows' media controls could not be shown: {Failure}", ex.GetType().Name);
        }
    }

    private void StopArtwork()
    {
        _artwork?.Cancel();
        _artwork?.Dispose();
        _artwork = null;
    }

    // The title's own time; seeking off while the player cannot seek (a guest of a Watch Party). Only finite times reach
    // TimeSpan; without a known length the timeline ends where playback is.
    private static void ShowTimeline(SystemMediaTransportControls controls, SystemMediaState state)
    {
        double position = double.IsFinite(state.PositionSeconds) ? Math.Max(0, state.PositionSeconds) : 0;
        double duration = double.IsFinite(state.DurationSeconds) && state.DurationSeconds > 0 ? state.DurationSeconds : position;
        var end = TimeSpan.FromSeconds(duration);
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = end,
            Position = TimeSpan.FromSeconds(Math.Min(position, duration)),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = state.CanSeek && state.DurationSeconds > 0 ? end : TimeSpan.Zero,
        });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        SystemMediaButton? button = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemMediaButton.Play,
            SystemMediaTransportControlsButton.Pause => SystemMediaButton.Pause,
            SystemMediaTransportControlsButton.Previous => SystemMediaButton.Previous,
            SystemMediaTransportControlsButton.Next => SystemMediaButton.Next,
            SystemMediaTransportControlsButton.Rewind => SystemMediaButton.Rewind,
            SystemMediaTransportControlsButton.FastForward => SystemMediaButton.FastForward,
            _ => null,
        };
        if (button is { } pressed)
        {
            Requested?.Invoke(this, new SystemMediaRequest(pressed));
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        Requested?.Invoke(this, new SystemMediaRequest(SystemMediaButton.Seek, args.RequestedPlaybackPosition.TotalSeconds));
}
