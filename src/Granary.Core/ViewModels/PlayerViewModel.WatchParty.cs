using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Granary.Core.Playback;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// A Watch Party's title (PlayRequest.WatchParty): the room plays, pauses and seeks it through its commands, and the
// viewer's play, pause and seek become requests to the room (WatchPartyPlaybackBinding). The player leaves when the room
// stops playing this title: the host stopped it, it finished, or the room ended. Meanwhile the panel (Party) shows the
// room over the picture.
public sealed partial class PlayerViewModel
{
    private WatchPartyPlaybackBinding? _party;
    private long _partyRevision;

    /// <summary>The room plays this title: the speed and version menus are the room's, not the viewer's.</summary>
    [ObservableProperty]
    public partial bool IsWatchParty { get; private set; }

    /// <summary>The viewer of a Watch Party may seek (the host).</summary>
    public bool CanSeekInRoom => !IsWatchParty || _watchParty?.Room?.IsHost == true;

    /// <summary>The Watch Party panel, while <see cref="IsWatchParty"/>; null in an app without Watch Party.</summary>
    public WatchPartyPanelViewModel? Party { get; }

    private WatchPartyPanelViewModel? InitializeWatchParty(IConfirmation? confirmation)
    {
        if (_watchParty is null)
        {
            return null;
        }
        var panel = new WatchPartyPanelViewModel(_watchParty, confirmation, _logger);
        panel.Said += (_, notice) => ShowPartyNotice(notice);
        return panel;
    }

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
        Party?.Attach();
    }

    private void EndWatchParty()
    {
        if (_party is not { } party)
        {
            return;
        }
        _party = null;
        party.Notice -= OnPartyNotice;
        party.Dispose();
        if (_watchParty is not null)
        {
            _watchParty.PropertyChanged -= OnPartyChanged;
        }
        Party?.Detach();
        IsWatchParty = false;
        // A correction rate never outlives the room, nor do the room's skip settings.
        _engine.SetSpeed(1);
        RetakeMarkerSettings();
    }

    // The viewer's play or pause in a room: asked of the room (refused, or held while it reconnects); the room's command
    // changes the player. Only a room that plays nothing leaves it to the player.
    private bool RequestRoomPlayPause() =>
        _party is { } party && party.RequestPlayPause(pause: !_engine.Current.IsPaused) != RoomTransportResult.NotInRoom;

    // The viewer's seek in a room: the host's goes to the room, a guest's is refused with the web's words.
    private bool RequestRoomSeek(double seconds) =>
        _party is { } party && party.RequestSeek(seconds) != RoomTransportResult.NotInRoom;

    // In a room nothing skips itself, as in Silo's apps: an intro the profile skips always is offered instead (to whoever
    // may seek), one it never skips stays unoffered, and credits and recaps play.
    private MarkerSettings RoomMarkers(MarkerSettings settings) => IsWatchParty
        ? settings with
        {
            IntroSkipMode = settings.IntroSkipMode == MarkerSettings.Always ? MarkerSettings.Ask : settings.IntroSkipMode,
            AutoSkipCredits = false,
            AutoSkipRecap = false,
        }
        : settings;

    private void OnPartyNotice(object? sender, string text)
    {
        if (sender == _party)
        {
            ShowPartyNotice(new PlaybackNotice("Watch Party", text, IsWarning: true));
        }
    }

    private void ShowPartyNotice(PlaybackNotice notice)
    {
        Notice = notice;
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
        // The host ended the room from the panel: on to Watch Party, as the room page's End goes. Only here, once.
        bool ended = party.Room is null && Party?.IsEndingRoom == true;
        Leave();
        _navigation.LeavePlayer();
        if (ended)
        {
            _navigation.NavigateTo(AppRoute.WatchParty);
        }
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
