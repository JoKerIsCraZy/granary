using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Texts;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The room page, as the web's (<c>pages/watchtogether/WatchTogetherRoomPage.tsx</c>): the status strip (live state and
/// count, the code and invite link, the host's choice between host picks and everyone votes, the connection, Leave and
/// End room); the stage, which changes with the room (a candidate from the shelf, what plays, the vote, the title up
/// next, or the open lobby), with the suggestions under it; the browse shelf; and the people and activity rail. When the
/// room starts a title the player opens by itself, once per start: the start is kept on the session, so the page shown
/// again (a new page each time, it is not cached) after the viewer left the player does not reopen it. A room that is over
/// (it ended, or this profile joined it on another device) is not shown: the page gives way to the hub, as in Silo's apps.
/// </summary>
public sealed partial class WatchPartyRoomViewModel : ObservableObject
{
    public static readonly TimeSpan MessageFor = TimeSpan.FromSeconds(4);

    private readonly WatchPartySession _party;
    private readonly WatchPartyFeature _feature;
    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyRoomViewModel> _logger;
    private bool _active;
    // The page's reads end with it.
    private CancellationTokenSource _life = new();
    private ITimer? _clock;
    private ITimer? _messageTimer;
    private string _phaseKey = "";
    private bool _leftForHub;

    public WatchPartyRoomViewModel(WatchPartySession party, WatchPartyFeature feature, ISessionManager session, CatalogApi catalog,
        ImageParameterCache images, INavigationService navigation, IUiDispatcher dispatcher, TimeProvider time, ILogger<WatchPartyRoomViewModel> logger)
    {
        _party = party;
        _feature = feature;
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        Shelf = new WatchPartyShelfViewModel(party, session, catalog, images, time, logger);
        Shelf.CardChosen += OnCardChosen;
        Candidate = new WatchPartyCandidateViewModel(party, session, catalog, images, logger);
        Candidate.Confirmed += OnCandidateConfirmed;
        Candidate.Dismissed += (_, _) => DismissCandidate();
    }

    public WatchPartyShelfViewModel Shelf { get; }

    public WatchPartyCandidateViewModel Candidate { get; }

    [ObservableProperty]
    public partial WatchPartyRoom? Room { get; private set; }

    /// <summary>A copy the viewer made, a suggestion that went in, or a host action that failed; it goes after a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(PlayLeaderCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>A card from the shelf is on the stage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlaying), nameof(ShowsVoting), nameof(ShowsStaged), nameof(ShowsEmptyLobby), nameof(ShowsHero),
        nameof(ShowsVotingEmpty), nameof(HeroEyebrow), nameof(HeroTone), nameof(HeroLine), nameof(ShowsSuggestions), nameof(ShowsInvite))]
    public partial bool HasCandidate { get; private set; }

    public bool HasMessage => Message.Length > 0;

    public bool HasRoom => Room is not null;

    public bool IsHost => Room?.IsHost == true;

    public bool IsGuest => Room is not null && !Room.IsHost;

    public bool IsVote => Room?.SelectionMode == WatchPartySelectionModes.Vote;

    public string Code => Room?.Code ?? "";

    public bool IsConnecting => Room is not null && !_party.IsConnected && _party.End is null;

    public bool IsLobbyEmpty => Room is { IsLobby: true, IsStaged: false };

    public bool IsStaged => Room?.IsStaged == true;

    public bool IsPlaying => Room?.IsPlaying == true;

    /// <summary>The room is over for this device: it ended, or another device took this profile's place in it.</summary>
    public bool IsEnded => _party.End is not null;

    /// <summary>The status strip, the stage and the people show: a room this device is in. One that is over shows none of them.</summary>
    public bool ShowsRoom => HasRoom && !IsEnded;

    /// <summary>The stage: what plays.</summary>
    public bool ShowsPlaying => !HasCandidate && Room is { IsPlaying: true, SelectedContentId: not null };

    /// <summary>The stage: the vote's leader, or that voting is open.</summary>
    public bool ShowsVoting => !HasCandidate && !IsPlaying && IsVote && Room is not null;

    /// <summary>The stage: the title up next in a host-pick lobby.</summary>
    public bool ShowsStaged => !HasCandidate && !IsPlaying && !IsVote && IsStaged;

    /// <summary>The stage: "ROOM IS OPEN / Nothing on yet".</summary>
    public bool ShowsEmptyLobby => !HasCandidate && !IsPlaying && !IsVote && IsLobbyEmpty;

    /// <summary>The host's name, or "The host" as the lobby's sentences name one without a name.</summary>
    public string HostName => Room?.Host?.DisplayName is { Length: > 0 } name ? name : AppText.Get("watchParty.theHost");

    /// <summary>"Live · 1 here", "Voting · 1 here" or "Playing · 3 here".</summary>
    public string PhaseLabel => Room is not { } room ? AppText.Get("web.watchTogether.connection.connecting")
        : room.IsPlaying ? AppText.Get("web.watchTogether.strip.playing", ("count", room.MemberCount), ("count_text", AppText.Number(room.MemberCount)))
        : IsVote ? AppText.Get("web.watchTogether.strip.voting", ("count", room.MemberCount), ("count_text", AppText.Number(room.MemberCount)))
        : AppText.Get("web.watchTogether.strip.live", ("count", room.MemberCount), ("count_text", AppText.Number(room.MemberCount)));

    /// <summary>"Host picks" or "Everyone votes".</summary>
    public string ModeLabel => IsVote ? AppText.Get("watchParty.options.everyoneVotes") : AppText.Get("web.watchTogether.common.hostPicks");

    /// <summary>The host switches how the room picks while nothing plays.</summary>
    public bool CanSwitchMode => IsHost && !IsPlaying;

    public bool ShowsModeLabel => Room is not null && !CanSwitchMode;

    /// <summary>"Connected", "Connecting…", "Reconnecting…" or "Disconnected".</summary>
    public string ConnectionLabel => _party.ConnectionState switch
    {
        _ when _party.End is not null => AppText.Get("watchParty.connection.disconnected"),
        WatchPartyConnectionState.Connected => AppText.Get("web.watchTogether.connection.connected"),
        WatchPartyConnectionState.Connecting when _wasConnected => AppText.Get("granary.watchParty.reconnecting"),
        WatchPartyConnectionState.Connecting => AppText.Get("web.watchTogether.connection.connecting"),
        _ => _wasConnected ? AppText.Get("granary.watchParty.reconnecting") : AppText.Get("watchParty.connection.disconnected"),
    };

    public bool IsLive => _party.IsConnected;

    private bool _wasConnected;

    /// <summary>The empty lobby's words: the host's moves, or what guests do meanwhile.</summary>
    public string LobbyText => IsHost
        ? AppText.Get("web.watchTogether.lobby.hostBody")
        : AppText.Get("web.watchTogether.lobby.guestBody", ("host", HostName));

    public string StagedEyebrow => IsHost
        ? AppText.Get("web.watchTogether.lobby.upNext")
        : AppText.Get("web.watchTogether.lobby.upNextGuest", ("host", HostName));

    public string StagedCaption
    {
        get
        {
            var (ready, total) = GuestsReady();
            return IsHost
                ? total > 0
                    ? AppText.Get("web.watchTogether.presence.ready", ("ready", ready), ("total", total), ("total_text", AppText.Number(total)))
                    : AppText.Get("web.watchTogether.lobby.waitingGuest")
                : AppText.Get("web.watchTogether.lobby.guestCaption",
                    ("host", HostName), ("ready", ready), ("total", total), ("total_text", AppText.Number(total)));
        }
    }

    public string StartLabel
    {
        get
        {
            var (ready, total) = GuestsReady();
            return total > 0
                ? AppText.Get("web.watchTogether.lobby.startEveryone", ("ready", ready), ("total", total), ("total_text", AppText.Number(total)))
                : AppText.Get("web.watchTogether.lobby.startAlone");
        }
    }

    /// <summary>Guests mark themselves ready in the lobby (the server's <c>lobby_ready</c>).</summary>
    public bool CanMarkReady => IsGuest && IsStaged && !IsVote && _feature.Capabilities?.LobbyReady != false;

    public bool IsSelfReady => Room?.Self?.LobbyReady == true;

    public string ReadyLabel => IsSelfReady ? AppText.Get("web.watchTogether.lobby.readyDone") : AppText.Get("watchParty.button.imReady");

    public bool CanStop => IsHost && _feature.Capabilities?.StopPlayback != false;

    public string StopHint => IsHost ? AppText.Get("web.watchTogether.playing.hostNote") : AppText.Get("web.watchTogether.playing.guestNote");

    public bool GuestsCanPause => Room?.GuestsCanPause == true;

    public string PolicyText => GuestsCanPause
        ? AppText.Get("web.watchTogether.inviteBar.guestsCanPause")
        : AppText.Get("web.watchTogether.inviteBar.hostControls");

    /// <summary>The invitation as a link to the server's web client, for the Link copy.</summary>
    public Uri? InviteLink => _party.InviteLink;

    /// <summary>The invite row's link as the web shows it, without its scheme.</summary>
    public string InviteText => InviteLink is { } link ? link.AbsoluteUri[(link.Scheme.Length + 3)..] : "";

    public bool HasInviteLink => InviteLink is not null;

    /// <summary>The invite row stands under a vote or a title up next.</summary>
    public bool ShowsInvite => ShowsVoting || ShowsStaged;

    /// <summary>Shows the room, follows it while the page shows, and opens the player when the room starts a title.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }
        _active = true;
        _leftForHub = false;
        if (_life.IsCancellationRequested)
        {
            _life.Dispose();
            _life = new CancellationTokenSource();
        }
        Shelf.Lifetime = _life.Token;
        _party.PropertyChanged += OnPartyChanged;
        _feature.PropertyChanged += OnFeatureChanged;
        _clock = _time.CreateTimer(_ => _dispatcher.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = _feature.RefreshAsync();
        Show();
        _ = Shelf.RefreshAsync(_life.Token);
    }

    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }
        _active = false;
        _party.PropertyChanged -= OnPartyChanged;
        _feature.PropertyChanged -= OnFeatureChanged;
        _clock?.Dispose();
        _clock = null;
        _messageTimer?.Dispose();
        _messageTimer = null;
        _life.Cancel();
        _heroShown = null;
        Candidate.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StartAsync() => HostAsync(_party.StartAsync, AppText.Get("web.watchTogether.actions.startFailed"));

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StopAsync() => HostAsync(_party.StopAsync, AppText.Get("web.watchTogether.actions.stopFailed"));

    /// <summary>The host switches the lobby to host picks or to voting.</summary>
    [RelayCommand]
    private Task SwitchModeAsync(string? mode) => mode is WatchPartySelectionModes.HostPick or WatchPartySelectionModes.Vote && mode != Room?.SelectionMode
        ? HostAsync(() => _party.SetSelectionModeAsync(mode), AppText.Get("web.watchTogether.actions.modeFailed"))
        : Task.CompletedTask;

    /// <summary>"Open the floor to votes".</summary>
    [RelayCommand]
    private Task OpenVotingAsync() => SwitchModeAsync(WatchPartySelectionModes.Vote);

    [RelayCommand]
    private Task ToggleGuestsCanPauseAsync() =>
        HostAsync(() => _party.SetGuestsCanPauseAsync(!GuestsCanPause), AppText.Get("web.watchTogether.actions.updateRoomFailed"));

    [RelayCommand]
    private async Task ToggleReadyAsync()
    {
        if (!await _party.SendAsync(WatchPartyFrames.LobbyReady(!IsSelfReady)))
        {
            Say(AppText.Get("web.watchTogether.actions.notConnected"));
        }
    }

    /// <summary>The host's "Change": the folded shelf opens.</summary>
    [RelayCommand]
    private void Change() => Shelf.IsOpen = true;

    /// <summary>Back into the room's playback, at its position.</summary>
    [RelayCommand]
    private void RejoinPlayback()
    {
        if (Room is { IsPlaying: true, SelectedContentId: { } content } room)
        {
            _party.StartedRevision = room.SelectionRevision;
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.WatchParty(content, room.SelectedFileId, room.SelectionRevision));
        }
    }

    /// <summary>Leaves the room on this device (a host who leaves ends it two minutes later unless they come back).</summary>
    [RelayCommand]
    private async Task LeaveAsync()
    {
        await _party.LeaveAsync();
        _navigation.NavigateTo(AppRoute.WatchParty);
    }

    /// <summary>The host ends the room for everyone (the page asked first); the page then goes to the hub, as on any end.</summary>
    public async Task EndRoomAsync()
    {
        try
        {
            await _party.EndRoomAsync();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not end the Watch Party");
            Say(WatchPartyText.Failed(AppText.Get("web.watchTogether.actions.endRoomFailed"), LibraryErrors.Describe(ex)));
        }
    }

    /// <summary>The page copied the code or link: it says so.</summary>
    public void Copied(string what) => Say(what);

    /// <summary>The room was just started from a title's page: it says what happened on the way, as the web's toasts did.</summary>
    public void Arrive(WatchPartyArrival arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        Say(arrival.Notice);
    }

    // True when the room took it.
    private async Task<bool> HostAsync(Func<Task> action, string failure)
    {
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "A host's action in the Watch Party failed");
            Say(WatchPartyText.Failed(failure, LibraryErrors.Describe(ex)));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAct() => !IsBusy && IsHost;

    // A message stays a few seconds, as the web's toasts.
    private void Say(string text)
    {
        Message = text;
        _messageTimer?.Dispose();
        _messageTimer = _time.CreateTimer(_ => _dispatcher.Post(() => Message = ""), null, MessageFor, Timeout.InfiniteTimeSpan);
    }

    private void OnPartyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchPartySession.Activity))
        {
            ShowActivity();
            return;
        }
        Show();
    }

    private void OnFeatureChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Show();

    private void Show()
    {
        Room = _party.Room;
        _wasConnected |= _party.IsConnected;
        // A candidate belongs to one phase and mode: starting, stopping or switching changes what confirming means.
        string phaseKey = $"{Room?.Phase}:{Room?.SelectionMode}";
        if (phaseKey != _phaseKey)
        {
            _phaseKey = phaseKey;
            DismissCandidate();
        }
        ShowMembers();
        ShowSuggestions();
        ShowActivity();
        foreach (string name in RoomProperties)
        {
            OnPropertyChanged(name);
        }
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PlayLeaderCommand.NotifyCanExecuteChanged();
        FollowShelf();
        ShowHero();
        ShowPlaying();
        AutoStart();
        LeaveIfOver();
    }

    // The room is over: the page gives way to the hub, which says why, as Silo's apps go back to theirs (Android's
    // WatchPartyLobbyScreen, Apple's WatchPartyHubView). A section, so Back does not come back to a room that is gone.
    // Posted, since the page may be still arriving; asked again when it runs, since the player over the page may have
    // gone to the hub meanwhile, or the room been left.
    private void LeaveIfOver()
    {
        if (!_active || !IsEnded || _leftForHub)
        {
            return;
        }
        _leftForHub = true;
        _dispatcher.Post(() =>
        {
            if (_active && IsEnded && _navigation.Current != AppRoute.WatchParty)
            {
                _navigation.ShowSection(AppRoute.WatchParty);
            }
        });
    }

    private static readonly string[] RoomProperties =
    [
        nameof(HasRoom), nameof(IsHost), nameof(IsGuest), nameof(IsVote), nameof(Code), nameof(IsConnecting), nameof(IsLobbyEmpty), nameof(IsStaged),
        nameof(IsPlaying), nameof(IsEnded), nameof(ShowsRoom), nameof(HostName), nameof(LobbyText),
        nameof(StagedEyebrow), nameof(StagedCaption), nameof(StartLabel), nameof(CanMarkReady), nameof(IsSelfReady), nameof(ReadyLabel),
        nameof(CanStop), nameof(StopHint), nameof(GuestsCanPause), nameof(PolicyText), nameof(InviteLink), nameof(InviteText), nameof(HasInviteLink),
        nameof(PhaseLabel), nameof(ModeLabel), nameof(CanSwitchMode), nameof(ShowsModeLabel), nameof(ConnectionLabel), nameof(IsLive),
        nameof(ShowsPlaying), nameof(ShowsVoting), nameof(ShowsStaged), nameof(ShowsEmptyLobby), nameof(ShowsInvite), nameof(PeopleCount),
        nameof(RailFooter), nameof(VotingEmptyText), nameof(LeaderCaption), nameof(ShowsHero), nameof(ShowsVotingEmpty), nameof(HeroEyebrow),
        nameof(HeroTone), nameof(HeroLine),
    ];

    // The shelf picks in a host-pick lobby for its host and suggests everywhere else; it folds where the stage leads.
    private void FollowShelf()
    {
        Shelf.Picks = IsHost && !IsVote && !IsPlaying;
        Shelf.IsCollapsible = (IsStaged && !IsVote && IsHost) || IsPlaying;
        if (!Shelf.IsCollapsible)
        {
            Shelf.IsOpen = false;
        }
        Shelf.FoldedLabel = IsPlaying ? AppText.Get("web.watchTogether.room.suggestAfter") : AppText.Get("web.watchTogether.room.changeUpNext");
        if (!HasCandidate)
        {
            Shelf.SelectedId = IsStaged ? Room?.SelectedContentId : null;
        }
        Shelf.FollowRoom();
    }

    // The room started a title (a new revision): the player opens once for it, as the web's room page does.
    private void AutoStart()
    {
        if (!_active || IsEnded || Room is not { IsPlaying: true, SelectedContentId: { } content } room
            || _party.StartedRevision == room.SelectionRevision)
        {
            return;
        }
        _party.StartedRevision = room.SelectionRevision;
        _navigation.NavigateTo(AppRoute.Player, PlayRequest.WatchParty(content, room.SelectedFileId, room.SelectionRevision));
    }

    private void Tick()
    {
        ShowPlaying();
        TickActivity();
    }

    private (int Ready, int Total) GuestsReady()
    {
        var guests = Room?.Members.Where(m => !m.IsHost).ToList() ?? [];
        return (guests.Count(m => m.LobbyReady), guests.Count);
    }
}
