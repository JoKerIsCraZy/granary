using System.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.Remote;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The open player as Silo's phone remote sees it (<see cref="IRemotePlayer"/>), for as long as the player page is open,
/// made beside <see cref="PlayerSystemMedia"/> and in the same way: the status is read from the view model, and a phone's
/// commands are the view model's own commands, never the engine's, so a Watch Party's rules hold as they do for the PC's
/// own buttons. As silo-roku's <c>PlayerRemote.bs</c>: "a command takes the path the viewer's own key or Options choice
/// takes". Every number a phone sends is checked and brought into range here, before the player sees it.
/// </summary>
/// <remarks>
/// The status leaves out the session id, the picture and the overview, as Silo's Roku app does: no signed address belongs
/// on this channel, and the phones look artwork up themselves from the content id. A file played with <c>--play</c> shows
/// as idle and takes no command: its name is nobody's business. A subtitle is named by its place in the list last sent,
/// which is kept, so a number goes back to the track it was sent for; one whose track has gone since is ignored.
/// </remarks>
public sealed class PlayerRemoteControl : IRemotePlayer, IDisposable
{
    // The title of a status while the title is not known yet, as silo-roku's PlayerRemote.bs and state.bs loading() send it.
    private const string LoadingTitle = "Loading";
    private const string AudioKind = "audio";
    private const string SubtitleKind = "subtitle";
    private const string Fit = "fit";
    private const string Fill = "fill";

    // What the status is read from. The position alone is not among them: the receiver's tick carries it.
    private static readonly HashSet<string> Shown =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.IsBuffering), nameof(PlayerViewModel.HasLeft),
        nameof(PlayerViewModel.DurationSeconds), nameof(PlayerViewModel.MediaInfo), nameof(PlayerViewModel.ErrorMessage),
        nameof(PlayerViewModel.AudioTracks), nameof(PlayerViewModel.SubtitleTracks), nameof(PlayerViewModel.IsSubtitleOn),
        nameof(PlayerViewModel.SubtitleDelayMs), nameof(PlayerViewModel.QualityOptions), nameof(PlayerViewModel.IsChangingPlan),
        nameof(PlayerViewModel.Speed), nameof(PlayerViewModel.IsFillingScreen), nameof(PlayerViewModel.Bar), nameof(PlayerViewModel.Volume),
        nameof(PlayerViewModel.IsMuted), nameof(PlayerViewModel.HasNextEpisode), nameof(PlayerViewModel.NextUpTitle),
        nameof(PlayerViewModel.NextUpCode), nameof(PlayerViewModel.IsWatchParty),
    ];

    private readonly PlayerViewModel _player;
    private readonly RemoteReceiver _receiver;
    private readonly IUiDispatcher _dispatcher;
    // The tracks of the status built last, in the order sent: the audio tracks by their ordinal, the subtitles by their own ids.
    private int _sentAudio;
    private IReadOnlyList<string> _sentSubtitles = [];
    private bool _disposed;

    /// <summary>Made on the UI thread by the player page, which disposes it where it disposes <see cref="PlayerSystemMedia"/>.</summary>
    public PlayerRemoteControl(PlayerViewModel player, RemoteReceiver receiver, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _player = player;
        _receiver = receiver;
        _dispatcher = dispatcher;
        _player.PropertyChanged += OnPlayerChanged;
        OnUiThread(() =>
        {
            if (!_disposed)
            {
                _receiver.Attach(this);
            }
        });
    }

    public event EventHandler? StateChanged;

    public SiloCastState State
    {
        get
        {
            if (_disposed || _player.HasLeft || _player.ContentId is null)
            {
                _sentAudio = 0;
                _sentSubtitles = [];
                return SiloCastState.Idle;
            }
            return Read();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _player.PropertyChanged -= OnPlayerChanged;
        OnUiThread(() => _receiver.Detach(this));
    }

    public RemoteCommandResult Apply(SiloCastMessage.Control command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var player = _player;
        if (_disposed || player.HasLeft)
        {
            // Nothing plays here any more, so there is nothing to stop.
            return command.Name == SiloCastControls.Stop && !_disposed ? RemoteCommandResult.Done : RemoteCommandResult.NotReady;
        }
        if (player.ContentId is null)
        {
            return RemoteCommandResult.NotReady;
        }
        switch (command.Name)
        {
            case SiloCastControls.Stop:
                // The player's Exit: it leaves also while the title still loads.
                player.CloseCommand.Execute(null);
                return RemoteCommandResult.Done;
            case SiloCastControls.SetVolume:
                return SetVolume(command.Volume);
            case SiloCastControls.SetMuted:
                return SetMuted(command.Enabled);
        }
        // As silo-roku's PlayerRemote.bs: a player that still loads, or that failed, is not ready for the rest.
        if (player.IsPreparing || player.IsFailed)
        {
            return RemoteCommandResult.NotReady;
        }
        return command.Name switch
        {
            SiloCastControls.Play => PlayOrPause(pause: false),
            SiloCastControls.Pause => PlayOrPause(pause: true),
            SiloCastControls.PlayPause => PlayOrPause(pause: !player.IsPaused),
            SiloCastControls.Seek => Seek(command.Seconds),
            SiloCastControls.SelectAudioTrack => SelectAudio(command.TrackId),
            SiloCastControls.SelectSubtitleTrack => SelectSubtitle(command.TrackId),
            SiloCastControls.SetPlaybackSpeed => SetSpeed(command.Speed),
            SiloCastControls.SetQuality => SetQuality(command.Value),
            SiloCastControls.SetVideoGravity => SetGravity(command.Value),
            SiloCastControls.SetSubtitleSyncMs => SetSubtitleDelay(command.Milliseconds),
            SiloCastControls.PlayNext => PlayNext(),
            // set_subtitle_position (subtitles are not moved on a phone's word), and every name Granary does not know.
            _ => RemoteCommandResult.Ignored,
        };
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && (string.IsNullOrEmpty(e.PropertyName) || Shown.Contains(e.PropertyName)))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private SiloCastState Read()
    {
        var player = _player;
        var audio = player.AudioTracks;
        // Off is no track: the phone sends no number for it.
        var subtitles = player.SubtitleTracks.Where(row => row.TrackId is not null).ToList();
        _sentAudio = audio.Count;
        _sentSubtitles = [.. subtitles.Select(row => row.TrackId!)];
        int shownSubtitle = subtitles.FindIndex(row => row.IsSelected);
        var info = player.MediaInfo;
        bool hasNext = HasNext;
        return new SiloCastState
        {
            ContentId = player.ContentId,
            Title = info.Title.Length > 0 ? info.Title : LoadingTitle,
            // The episode line; a movie's year is not one.
            Subtitle = info.Kind == PlaybackMediaKind.Episode && info.Subtitle.Length > 0 ? info.Subtitle : null,
            IsPlaying = player.IsPlaying && !player.IsPaused,
            IsLoading = player.IsPreparing,
            IsBuffering = player.IsBuffering,
            CurrentTime = Seconds(player.PositionSeconds),
            Duration = Seconds(player.DurationSeconds),
            AudioTracks = [.. audio.Select(track => new SiloCastTrack(AudioKind, track.Ordinal, track.Title, OrNull(track.Meta)))],
            SubtitleTracks = [.. subtitles.Select((row, index) => new SiloCastTrack(SubtitleKind, index, row.Title, SubtitleDetail(row)))],
            SelectedAudioTrackId = audio.FirstOrDefault(track => track.IsSelected)?.Ordinal,
            SelectedSubtitleTrackId = shownSubtitle >= 0 ? shownSubtitle : null,
            QualityOptions = [.. player.QualityOptions.Select(option => new SiloCastQuality(option.Preference, option.Label, OrNull(option.Bitrate)))],
            ActiveQualityId = player.QualityOptions.FirstOrDefault(option => option.IsSelected)?.Preference ?? QualityOption.Auto,
            IsQualitySwitching = player.IsChangingPlan,
            PlaybackSpeed = double.IsFinite(player.Speed) && player.Speed > 0 ? player.Speed : 1,
            VideoGravity = player.IsFillingScreen ? Fill : Fit,
            SupportsVideoGravity = player.Bar.Fill || player.IsFillingScreen,
            // As on the web player, the delay is for a subtitle that shows.
            SubtitleSyncMs = player.IsSubtitleOn ? player.SubtitleDelayMs : null,
            SupportsSubtitleDelay = player.IsSubtitleOn,
            Volume = double.IsFinite(player.Volume) ? Math.Clamp(player.Volume / 100, 0, 1) : 1,
            IsMuted = player.IsMuted,
            HasNextEpisode = hasNext,
            NextEpisodeTitle = hasNext ? OrNull(player.NextUpTitle) ?? OrNull(player.NextUpCode) : null,
            Error = player.IsFailed ? OrNull(player.ErrorMessage) : null,
        };
    }

    // In a Watch Party the room decides what plays next.
    private bool HasNext => _player.HasNextEpisode && !_player.IsWatchParty;

    // The player's pause button: in a Watch Party a request to the room, which the room may refuse.
    private RemoteCommandResult PlayOrPause(bool pause)
    {
        if (!_player.IsPlaying)
        {
            return RemoteCommandResult.NotReady;
        }
        if (!_player.CanPauseInRoom)
        {
            return RemoteCommandResult.Failed;
        }
        if (_player.IsPaused == pause)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.TogglePauseCommand.Execute(null);
        return RemoteCommandResult.Done;
    }

    // Within the title: a title of unknown length is not sought at all, as nothing bounds the number.
    private RemoteCommandResult Seek(double? seconds)
    {
        if (seconds is not { } target || !double.IsFinite(target))
        {
            return RemoteCommandResult.Ignored;
        }
        if (!_player.IsPlaying || !_player.CanSeek)
        {
            return RemoteCommandResult.NotReady;
        }
        if (!_player.CanSeekInRoom)
        {
            return RemoteCommandResult.Failed;
        }
        double length = _player.DurationSeconds;
        if (!double.IsFinite(length) || length <= 0)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.SeekTo(Math.Clamp(target, 0, length));
        return RemoteCommandResult.Done;
    }

    // 0 to 1 on the wire, 0 to 100 in the player; the slider's own path, so it is kept as the PC's own change is.
    private RemoteCommandResult SetVolume(double? volume)
    {
        if (volume is not { } level || !double.IsFinite(level))
        {
            return RemoteCommandResult.Ignored;
        }
        _player.Volume = Math.Round(Math.Clamp(level, 0, 1) * 100, 1);
        return RemoteCommandResult.Done;
    }

    private RemoteCommandResult SetMuted(bool? enabled)
    {
        if (enabled is not { } muted || _player.IsMuted == muted)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.ToggleMuteCommand.Execute(null);
        return RemoteCommandResult.Done;
    }

    // An audio track's number is its ordinal, the one the player's menu passes on.
    private RemoteCommandResult SelectAudio(long? trackId)
    {
        var tracks = _player.AudioTracks;
        if (trackId is not { } id || id < 0 || id >= _sentAudio || id >= tracks.Count)
        {
            return RemoteCommandResult.Ignored;
        }
        int ordinal = (int)id;
        if (tracks[ordinal].IsSelected)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.SelectAudio(ordinal);
        return RemoteCommandResult.Done;
    }

    // No number turns subtitles off; a number goes back through the list last sent to the track's own id.
    private RemoteCommandResult SelectSubtitle(long? trackId)
    {
        if (trackId is not { } id)
        {
            if (!_player.IsSubtitleOn)
            {
                return RemoteCommandResult.Ignored;
            }
            _player.SelectSubtitle(null);
            return RemoteCommandResult.Done;
        }
        var sent = _sentSubtitles;
        if (id < 0 || id >= sent.Count)
        {
            return RemoteCommandResult.Ignored;
        }
        string own = sent[(int)id];
        var row = _player.SubtitleTracks.FirstOrDefault(option => option.TrackId == own);
        if (row is null || row.IsSelected)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.SelectSubtitle(own);
        return RemoteCommandResult.Done;
    }

    // The nearest of the player's speeds; none at or below zero. A Watch Party plays at the room's speed.
    private RemoteCommandResult SetSpeed(double? speed)
    {
        if (speed is not { } wanted || !double.IsFinite(wanted) || wanted <= 0)
        {
            return RemoteCommandResult.Ignored;
        }
        if (_player.IsWatchParty)
        {
            return RemoteCommandResult.Failed;
        }
        double stop = PlaybackSpeeds.Stops[PlaybackSpeeds.NearestStop(wanted)];
        if (stop.Equals(_player.Speed))
        {
            return RemoteCommandResult.Ignored;
        }
        _player.SetSpeed(stop);
        return RemoteCommandResult.Done;
    }

    // A quality is named by the option's own preference; one the menu does not offer is ignored.
    private RemoteCommandResult SetQuality(string? value)
    {
        var option = string.IsNullOrEmpty(value)
            ? null
            : _player.QualityOptions.FirstOrDefault(candidate => string.Equals(candidate.Preference, value, StringComparison.Ordinal));
        if (option is null || option.IsSelected)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.SelectQuality(option.Preference);
        return RemoteCommandResult.Done;
    }

    // The player's Fill screen, which fills only while Settings shows its button.
    private RemoteCommandResult SetGravity(string? value)
    {
        bool fill = value == Fill;
        if ((!fill && value != Fit) || _player.IsFillingScreen == fill || (fill && !_player.Bar.Fill))
        {
            return RemoteCommandResult.Ignored;
        }
        _player.ToggleFillCommand.Execute(null);
        return RemoteCommandResult.Done;
    }

    // Within the delay's limits, for a subtitle that shows.
    private RemoteCommandResult SetSubtitleDelay(int? milliseconds)
    {
        if (milliseconds is not { } wanted || !_player.IsSubtitleOn)
        {
            return RemoteCommandResult.Ignored;
        }
        int delay = Math.Clamp(wanted, -PlaybackPreferencesReader.MaxSubtitleDelayMs, PlaybackPreferencesReader.MaxSubtitleDelayMs);
        if (delay == _player.SubtitleDelayMs)
        {
            return RemoteCommandResult.Ignored;
        }
        _player.ChangeSubtitleDelay(delay - _player.SubtitleDelayMs);
        return RemoteCommandResult.Done;
    }

    // The player's next-episode button; without a next episode the phone is told so.
    private RemoteCommandResult PlayNext()
    {
        if (!HasNext)
        {
            return RemoteCommandResult.Failed;
        }
        if (!_player.PlayNextEpisodeCommand.CanExecute(null))
        {
            return RemoteCommandResult.NotReady;
        }
        _player.PlayNextEpisodeCommand.Execute(null);
        return RemoteCommandResult.Done;
    }

    private static double Seconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    private static string? OrNull(string text) => text.Length > 0 ? text : null;

    // The chips of the web player's subtitle menu, and the line under the row: two tracks of one language differ by them.
    private static string? SubtitleDetail(SubtitleTrackOption row) =>
        OrNull(string.Join(" · ", new[] { row.Format, row.Source, row.Detail }.Where(part => part.Length > 0)));
}
