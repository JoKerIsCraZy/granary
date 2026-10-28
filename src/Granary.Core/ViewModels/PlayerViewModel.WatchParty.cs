using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.WatchParty;
using Granary.Core.Playback;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// A Watch Party's title (PlayRequest.WatchParty): the room plays, pauses and seeks it through its commands, and the
// viewer's play, pause and seek become requests to the room (WatchPartyPlaybackBinding). The player leaves when the room
// stops playing this title: the host stopped it, it finished, or the room ended.
public sealed partial class PlayerViewModel
{
    private WatchPartyPlaybackBinding? _party;
    private long _partyRevision;

    /// <summary>The room plays this title: the speed and version menus are the room's, not the viewer's.</summary>
    [ObservableProperty]
    public partial bool IsWatchParty { get; private set; }

    /// <summary>The viewer of a Watch Party may seek (the host).</summary>
    public bool CanSeekInRoom => !IsWatchParty || _watchParty?.Room?.IsHost == true;

    private void BeginWatchParty(PlayRequest request)
    {
        if (!request.IsWatchParty || _watchParty is null)
        {
            return;
        }
        IsWatchParty = true;
        _partyRevision = request.SelectionRevision;
        _party = new WatchPartyPlaybackBinding(_watchParty, new RoomPlayer(this), _dispatcher, _time, _logger);
        _party.Notice += OnPartyNotice;
        _party.Start();
        _watchParty.PropertyChanged += OnPartyChanged;
    }

    private void EndWatchParty()
    {
        if (_party is { } party)
        {
            _party = null;
            party.Notice -= OnPartyNotice;
            party.Dispose();
        }
        if (_watchParty is not null)
        {
            _watchParty.PropertyChanged -= OnPartyChanged;
        }
        IsWatchParty = false;
        // A correction rate never outlives the room.
        _engine.SetSpeed(1);
    }

    // The viewer's play or pause in a room: asked of the room; the room's command changes the player.
    private bool RequestRoomPlayPause()
    {
        if (_party is not { } party)
        {
            return false;
        }
        party.RequestPlayPause(pause: !_engine.Current.IsPaused);
        return true;
    }

    // The viewer's seek in a room: the host's goes to the room, a guest's is refused with the web's words.
    private bool RequestRoomSeek(double seconds)
    {
        if (_party is not { } party)
        {
            return false;
        }
        party.RequestSeek(seconds);
        return true;
    }

    private void OnPartyNotice(object? sender, string text)
    {
        if (sender != _party)
        {
            return;
        }
        Notice = new PlaybackNotice("Watch Party", text, IsWarning: true);
        StopNoticeTimer();
        if (!_noticesHeld)
        {
            StartNoticeTimer();
        }
    }

    // The room left this title (stopped, finished, another title, ended): the player gives way to the room page.
    private void OnPartyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_party is null || _watchParty is not { } party || e.PropertyName is not (nameof(WatchPartySession.Room) or nameof(WatchPartySession.End)))
        {
            return;
        }
        OnPropertyChanged(nameof(CanSeekInRoom));
        if (party.Room is { IsPlaying: true } room && room.SelectionRevision == _partyRevision && party.End is null)
        {
            return;
        }
        _logger.LogInformation("The Watch Party left the title; the player closes");
        Leave();
        _navigation.LeavePlayer();
    }

    /// <summary>The player as the room's commands see it.</summary>
    private sealed class RoomPlayer(PlayerViewModel player) : IRoomPlayer
    {
        public RoomPlayerObservation Observe()
        {
            var state = player._engineState;
            var playback = player._playback;
            bool reconnecting = playback?.Status.Phase == ServerPlaybackPhase.Reconnecting;
            bool seeking = player._seekTarget is not null && Environment.TickCount64 - player._seekTargetAt < SeekTargetMilliseconds;
            bool busy = player.Phase == PlayerPhase.Preparing || !state.IsLoaded || !state.HasStarted || seeking;
            bool buffering = state.IsBuffering || reconnecting;
            return new RoomPlayerObservation(playback?.SessionId, player.PositionSeconds, IntendsToPlay: !state.IsPaused,
                IsAdvancing: !state.IsPaused && !buffering && !busy && player.Phase == PlayerPhase.Playing, IsBuffering: buffering, IsBusy: busy,
                IsEnded: player.Phase == PlayerPhase.Ended || state.IsAtEnd);
        }

        public void SeekTo(double sourceSeconds) => player.SeekLocally(sourceSeconds);

        public void SetPlaying(bool playing) => player._engine.SetPaused(!playing);

        public void SetCorrectionRate(double? rate) => player._engine.SetSpeed(rate ?? 1);

        public bool CanReachInPlace(double sourceSeconds) => player._playback?.CanReachInPlace(sourceSeconds) == true;
    }
}
