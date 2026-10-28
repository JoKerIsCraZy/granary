using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The room page, as the web's (<c>pages/watchtogether/WatchTogetherRoomPage.tsx</c>): the status strip (live state and
/// count, the code and invite link, the host's choice between host picks and everyone votes, the connection, Leave and
/// End room); the stage, which changes with the room (a candidate from the shelf, what plays, the vote, the title up
/// next, or the open lobby), with the suggestions under it; the browse shelf; and the people and activity rail. When the
/// room starts a title the player opens by itself, once per start: the start is kept on the session, so the page shown
/// again (a new page each time, it is not cached) after the viewer left the player does not reopen it.
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
    private ITimer? _clock;
    private ITimer? _messageTimer;
    private string _phaseKey = "";

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

    public bool IsEnded => _party.End is not null;

    public bool IsReplaced => _party.End?.Replaced == true;

    public bool IsClosed => IsEnded && !IsReplaced;

    /// <summary>The stage: what plays.</summary>
    public bool ShowsPlaying => !HasCandidate && Room is { IsPlaying: true, SelectedContentId: not null };

    /// <summary>The stage: the vote's leader, or that voting is open.</summary>
    public bool ShowsVoting => !HasCandidate && !IsPlaying && IsVote && Room is not null;

    /// <summary>The stage: the title up next in a host-pick lobby.</summary>
    public bool ShowsStaged => !HasCandidate && !IsPlaying && !IsVote && IsStaged;

    /// <summary>The stage: "ROOM IS OPEN / Nothing on yet".</summary>
    public bool ShowsEmptyLobby => !HasCandidate && !IsPlaying && !IsVote && IsLobbyEmpty;

    /// <summary>The room's end in the web's words.</summary>
    public string EndedText => _party.End?.Reason switch
    {
        null => "",
        "not_found" => "Room not found.",
        "ended" => "That watch party is no longer active.",
        "forbidden" => "You don't have access to this watch party.",
        "replaced" => _party.End?.Message ?? WatchPartyFrames.ReplacedText,
        _ => "The room has ended.",
    };

    public string HostName => Room?.Host?.DisplayName is { Length: > 0 } name ? name : "The host";

    /// <summary>"Live · 1 here", "Voting · 1 here" or "Playing · 3 here".</summary>
    public string PhaseLabel => Room is not { } room ? "Connecting…"
        : room.IsPlaying ? $"Playing · {room.MemberCount} here"
        : IsVote ? $"Voting · {room.MemberCount} here"
        : $"Live · {room.MemberCount} here";

    /// <summary>"Host picks" or "Everyone votes".</summary>
    public string ModeLabel => IsVote ? "Everyone votes" : "Host picks";

    /// <summary>The host switches how the room picks while nothing plays.</summary>
    public bool CanSwitchMode => IsHost && !IsPlaying;

    public bool ShowsModeLabel => Room is not null && !CanSwitchMode;

    /// <summary>"Connected", "Connecting…", "Reconnecting…" or "Disconnected".</summary>
    public string ConnectionLabel => _party.ConnectionState switch
    {
        _ when _party.End is not null => "Disconnected",
        WatchPartyConnectionState.Connected => "Connected",
        WatchPartyConnectionState.Connecting when _wasConnected => "Reconnecting…",
        WatchPartyConnectionState.Connecting => "Connecting…",
        _ => _wasConnected ? "Reconnecting…" : "Disconnected",
    };

    public bool IsLive => _party.IsConnected;

    private bool _wasConnected;

    /// <summary>The empty lobby's words: the host's moves, or what guests do meanwhile.</summary>
    public string LobbyText => IsHost
        ? "Pick something below and it lands here for everyone to see. Or let the room decide."
        : $"{HostName} is deciding. Anything you pick below goes in as a suggestion.";

    public string StagedEyebrow => IsHost ? "Up next" : $"Up next · {HostName} queued this";

    public string StagedCaption
    {
        get
        {
            var (ready, total) = GuestsReady();
            return IsHost
                ? total > 0 ? $"{ready} of {total} ready" : "Waiting for a guest to join"
                : $"Starts when {HostName} presses play · {ready} of {total} ready";
        }
    }

    public string StartLabel
    {
        get
        {
            var (ready, total) = GuestsReady();
            return total > 0 ? $"Start for everyone · {ready}/{total} ready" : "Start alone anyway";
        }
    }

    /// <summary>Guests mark themselves ready in the lobby (the server's <c>lobby_ready</c>).</summary>
    public bool CanMarkReady => IsGuest && IsStaged && !IsVote && _feature.Capabilities?.LobbyReady != false;

    public bool IsSelfReady => Room?.Self?.LobbyReady == true;

    public string ReadyLabel => IsSelfReady ? "Ready ✓" : "I'm ready";

    public bool CanStop => IsHost && _feature.Capabilities?.StopPlayback != false;

    public string StopHint => IsHost
        ? "Playback keeps going for the others. Stopping brings everyone back here to pick something else."
        : "Playback keeps going for the others. Rejoining puts you at the room's position.";

    public bool GuestsCanPause => Room?.GuestsCanPause == true;

    public string PolicyText => GuestsCanPause ? "Guests can pause" : "Host controls playback";

    /// <summary>The invitation as a link to the server's web client, for the Link copy.</summary>
    public Uri? InviteLink => Room is { } room && _session.ActiveServer is { } server ? WatchPartyApi.InviteLink(server.BaseUri, room) : null;

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
        _party.PropertyChanged += OnPartyChanged;
        _feature.PropertyChanged += OnFeatureChanged;
        _clock = _time.CreateTimer(_ => _dispatcher.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = _feature.RefreshAsync();
        Show();
        _ = Shelf.RefreshAsync();
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
        Candidate.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StartAsync() => HostAsync(_party.StartAsync, "Could not start playback");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StopAsync() => HostAsync(_party.StopAsync, "Could not stop playback");

    /// <summary>The host switches the lobby to host picks or to voting.</summary>
    [RelayCommand]
    private Task SwitchModeAsync(string? mode) => mode is WatchPartySelectionModes.HostPick or WatchPartySelectionModes.Vote && mode != Room?.SelectionMode
        ? HostAsync(() => _party.SetSelectionModeAsync(mode), "Could not change how the room picks")
        : Task.CompletedTask;

    /// <summary>"Open the floor to votes".</summary>
    [RelayCommand]
    private Task OpenVotingAsync() => SwitchModeAsync(WatchPartySelectionModes.Vote);

    [RelayCommand]
    private Task ToggleGuestsCanPauseAsync() => HostAsync(() => _party.SetGuestsCanPauseAsync(!GuestsCanPause), "Could not change who controls playback");

    [RelayCommand]
    private async Task ToggleReadyAsync()
    {
        if (!await _party.SendAsync(WatchPartyFrames.LobbyReady(!IsSelfReady)))
        {
            Say(WatchPartyPlaybackBinding.ReconnectingText);
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

    /// <summary>The host ends the room for everyone (the page asked first).</summary>
    public async Task EndRoomAsync()
    {
        try
        {
            await _party.EndRoomAsync();
            _navigation.NavigateTo(AppRoute.WatchParty);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not end the Watch Party");
            Say("Could not end the room. " + LibraryErrors.Describe(ex));
        }
    }

    /// <summary>Claims the membership back after another device took it over.</summary>
    [RelayCommand]
    private void Rejoin() => _party.Rejoin();

    /// <summary>The page copied the code or link: it says so.</summary>
    public void Copied(string what) => Say(what);

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
            _logger.LogWarning(ex, "{Failure}", failure);
            Say($"{failure}. {LibraryErrors.Describe(ex)}");
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
    }

    private static readonly string[] RoomProperties =
    [
        nameof(HasRoom), nameof(IsHost), nameof(IsGuest), nameof(IsVote), nameof(Code), nameof(IsConnecting), nameof(IsLobbyEmpty), nameof(IsStaged),
        nameof(IsPlaying), nameof(IsEnded), nameof(IsReplaced), nameof(IsClosed), nameof(EndedText), nameof(HostName), nameof(LobbyText),
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
        Shelf.FoldedLabel = IsPlaying ? "Suggest something for after" : "Change what's up next";
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
