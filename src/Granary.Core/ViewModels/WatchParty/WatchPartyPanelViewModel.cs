using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.Playback;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The Watch Party panel over the player, as the web's (<c>player/components/WatchTogetherPanel.tsx</c>): the connection,
/// the room code and how many watch, who controls playback, each viewer with what their player is doing, and for the
/// host Invite, Allow Pause or Host Only, Stop and End. It acts through the room the app is in
/// (<see cref="WatchPartySession"/>), as the room page does, and says what happened in the web's toast words while the
/// player is there to show it: Stop and End close the player, the room's lobby or Watch Party then shows what they did.
/// </summary>
public sealed partial class WatchPartyPanelViewModel : ObservableObject
{
    private const string Title = "Watch Party";

    private readonly WatchPartySession _party;
    private readonly IConfirmation? _confirmation;
    private readonly ILogger _logger;
    private bool _attached;
    // Counts the times the panel was shown for a title: what a call made under one says is not said over the next.
    private long _attachment;
    // The host's calls on their way: each keeps the buttons waiting until its own answer is back.
    private int _calls;

    public WatchPartyPanelViewModel(WatchPartySession party, IConfirmation? confirmation, ILogger logger)
    {
        _party = party;
        _confirmation = confirmation;
        _logger = logger;
    }

    /// <summary>What an action did or why it failed, for the player to show as its notice.</summary>
    public event EventHandler<PlaybackNotice>? Said;

    /// <summary>The room as the panel last showed it.</summary>
    [ObservableProperty]
    public partial WatchPartyRoom? Room { get; private set; }

    public ObservableCollection<WatchPartyViewerRow> Viewers { get; } = [];

    /// <summary>A host's call is on its way.</summary>
    public bool IsBusy => _calls > 0;

    /// <summary>The host ended the room from here and this device is leaving it: the player goes on to Watch Party.</summary>
    internal bool IsEndingRoom { get; private set; }

    /// <summary>The panel shows while the player plays the room's title and the room lasts, as on the web.</summary>
    public bool IsShown => _attached && Room is not null && _party.End is null;

    public bool HasViewers => Viewers.Count > 0;

    /// <summary>This profile may manage the room (the web's <c>self_can_manage_room</c>): the host's buttons show.</summary>
    public bool IsHost => Room?.SelfCanManageRoom == true;

    /// <summary>The room's code, "..." until there is one.</summary>
    public string Code => Room?.Code is { Length: > 0 } code ? code : "...";

    /// <summary>"1 viewer", "3 viewers".</summary>
    public string ViewersText => Room is { } room ? $"{room.MemberCount} {(room.MemberCount == 1 ? "viewer" : "viewers")}" : "";

    /// <summary>Who may play and pause, from where this viewer stands.</summary>
    public string PolicyText => Room switch
    {
        null => "Syncing room state",
        { SelfCanManageRoom: true, GuestsCanPause: true } => "Guests can pause & resume",
        { SelfCanManageRoom: true } => "Only you control playback",
        { SelfCanControlTransport: true } => "You can pause & resume",
        _ => "Host controls playback",
    };

    /// <summary>The host's policy switch, named for what it turns the room into.</summary>
    public string PolicyButtonText => Room?.GuestsCanPause == true ? "Host Only" : "Allow Pause";

    /// <summary>The connection as the dot shows it; a room that ended is disconnected.</summary>
    public WatchPartyConnectionState ConnectionState => _party.End is null ? _party.ConnectionState : WatchPartyConnectionState.Disconnected;

    /// <summary>"Connected", "Connecting…" or "Disconnected".</summary>
    public string ConnectionText => ConnectionState switch
    {
        WatchPartyConnectionState.Connected => "Connected",
        WatchPartyConnectionState.Connecting => "Connecting…",
        _ => "Disconnected",
    };

    /// <summary>Follows the room while the player plays its title.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        _attachment++;
        _party.PropertyChanged += OnPartyChanged;
        Show();
    }

    /// <summary>Lets go of the room. A call still on its way keeps its guard until its answer is back.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _party.PropertyChanged -= OnPartyChanged;
        OnPropertyChanged(nameof(IsShown));
    }

    /// <summary>
    /// Copies the invite link with <paramref name="copy"/> (the clipboard, which the page has), once the room has one.
    /// </summary>
    public void CopyInvite(Func<string, bool> copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (_party.InviteLink is not { } link)
        {
            Say("Invite link is not ready yet.", _attachment);
            return;
        }
        if (copy(link.AbsoluteUri))
        {
            Say($"Invite copied. Room code {Room?.Code}".Trim(), _attachment);
        }
        else
        {
            Say("Failed to copy invite link", _attachment, warning: true);
        }
    }

    /// <summary>Lets guests pause and resume, or takes it back.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task TogglePolicyAsync()
    {
        long attachment = _attachment;
        bool allow = Room?.GuestsCanPause != true;
        if (await HostAsync(() => _party.SetGuestsCanPauseAsync(allow), "Failed to update room", attachment))
        {
            Say(_party.Room?.GuestsCanPause == true ? "Guests can now pause and resume" : "Room is now host controlled", attachment);
        }
    }

    /// <summary>
    /// Stops for everyone; the room stays open and everyone goes back to it to pick something else. The player closes
    /// with the room's lobby, so only a failure is said here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StopAsync() => HostAsync(_party.StopAsync, "Could not stop playback", _attachment);

    /// <summary>
    /// Ends the room for everyone once the host confirms, in the web's words (<c>EndWatchPartyDialog</c>). The player then
    /// closes and goes on to Watch Party; only a failure is said here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EndAsync()
    {
        if (_confirmation is null || Room is not { } room)
        {
            return;
        }
        long attachment = _attachment;
        if (!await _confirmation.AskAsync("End watch party?", "End the watch party for everyone?", "End Party"))
        {
            return;
        }
        // Asked again once answered: the player may have closed, the room stopped or ended, or another call started.
        if (attachment != _attachment || !_attached || !CanAct() || _party.Room?.RoomId != room.RoomId || !_party.IsInRoom)
        {
            return;
        }
        IsEndingRoom = true;
        try
        {
            await HostAsync(_party.EndRoomAsync, "Failed to end room", attachment);
        }
        finally
        {
            IsEndingRoom = false;
        }
    }

    private bool CanAct() => !IsBusy && IsHost;

    // True when the room took the call.
    private async Task<bool> HostAsync(Func<Task<bool>> action, string failure, long attachment)
    {
        Count(1);
        try
        {
            return await action();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Failure}", failure);
            Say(Failed(failure, ex), attachment, warning: true);
            return false;
        }
        finally
        {
            Count(-1);
        }
    }

    private void Count(int change)
    {
        _calls += change;
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        TogglePolicyCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
    }

    // The web's words for what failed; a server out of reach or busy says so too.
    private static string Failed(string failure, Exception ex) => ex switch
    {
        SiloApiException { Status: 429 or 503 } => $"{failure}. {LibraryErrors.Busy}",
        HttpRequestException or OperationCanceledException => $"{failure}. {LibraryErrors.Unreachable}",
        _ => failure,
    };

    // Said only while the panel shows for the title the call was made under.
    private void Say(string text, long attachment, bool warning = false)
    {
        if (_attached && attachment == _attachment)
        {
            Said?.Invoke(this, new PlaybackNotice(Title, text, warning));
        }
    }

    private void OnPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WatchPartySession.Room) or nameof(WatchPartySession.ConnectionState) or nameof(WatchPartySession.End))
        {
            Show();
        }
    }

    private void Show()
    {
        Room = _party.Room;
        ShowViewers();
        foreach (string name in RoomProperties)
        {
            OnPropertyChanged(name);
        }
        NotifyCommands();
    }

    private static readonly string[] RoomProperties =
    [
        nameof(IsShown), nameof(IsHost), nameof(Code), nameof(ViewersText), nameof(PolicyText), nameof(PolicyButtonText), nameof(ConnectionState),
        nameof(ConnectionText),
    ];

    // Rows change only when what they show changes: a snapshot comes with every member's buffering.
    private void ShowViewers()
    {
        var rows = Room?.Members.Select(m => new WatchPartyViewerRow(m.Key, m.IsSelf ? m.DisplayName + " (you)" : m.DisplayName, StateOf(m))).ToList() ?? [];
        if (rows.SequenceEqual(Viewers))
        {
            return;
        }
        Viewers.Clear();
        foreach (var row in rows)
        {
            Viewers.Add(row);
        }
        OnPropertyChanged(nameof(HasViewers));
    }

    private static string StateOf(WatchPartyMember member) =>
        member.IsBuffering ? "Buffering" : member.IsSyncing ? "Syncing" : member.IsReady ? "Ready" : "Connected";
}
