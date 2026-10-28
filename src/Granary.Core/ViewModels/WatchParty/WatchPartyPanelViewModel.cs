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
/// (<see cref="WatchPartySession"/>), as the room page does, and says what happened in the web's toast words.
/// </summary>
public sealed partial class WatchPartyPanelViewModel : ObservableObject
{
    private const string Title = "Watch Party";

    private readonly WatchPartySession _party;
    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private bool _attached;

    public WatchPartyPanelViewModel(WatchPartySession party, INavigationService navigation, ILogger logger)
    {
        _party = party;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>What an action did or why it failed, for the player to show as its notice.</summary>
    public event EventHandler<PlaybackNotice>? Said;

    /// <summary>The room as the panel last showed it.</summary>
    [ObservableProperty]
    public partial WatchPartyRoom? Room { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePolicyCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<WatchPartyViewerRow> Viewers { get; } = [];

    public bool HasViewers => Viewers.Count > 0;

    public bool IsHost => Room?.IsHost == true;

    /// <summary>The room's code, "..." until there is one.</summary>
    public string Code => Room?.Code is { Length: > 0 } code ? code : "...";

    /// <summary>"1 viewer", "3 viewers".</summary>
    public string ViewersText => Room is { } room ? $"{room.MemberCount} {(room.MemberCount == 1 ? "viewer" : "viewers")}" : "";

    /// <summary>Who may play and pause, from where this viewer stands.</summary>
    public string PolicyText => Room switch
    {
        null => "Syncing room state",
        { IsHost: true, GuestsCanPause: true } => "Guests can pause & resume",
        { IsHost: true } => "Only you control playback",
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
        _party.PropertyChanged += OnPartyChanged;
        Show();
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _party.PropertyChanged -= OnPartyChanged;
        IsBusy = false;
    }

    /// <summary>
    /// Copies the invite link with <paramref name="copy"/> (the clipboard, which the page has), once the room has one.
    /// </summary>
    public void CopyInvite(Func<string, bool> copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (_party.InviteLink is not { } link)
        {
            Say("Invite link is not ready yet.");
            return;
        }
        if (copy(link.AbsoluteUri))
        {
            Say($"Invite copied. Room code {Room?.Code}".Trim());
        }
        else
        {
            Say("Failed to copy invite link", warning: true);
        }
    }

    /// <summary>Lets guests pause and resume, or takes it back.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task TogglePolicyAsync()
    {
        if (await HostAsync(() => _party.SetGuestsCanPauseAsync(Room?.GuestsCanPause != true), "Failed to update room"))
        {
            Say(_party.Room?.GuestsCanPause == true ? "Guests can now pause and resume" : "Room is now host controlled");
        }
    }

    /// <summary>Stops for everyone; the room stays open and everyone goes back to it to pick something else.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task StopAsync()
    {
        if (await HostAsync(_party.StopAsync, "Could not stop playback"))
        {
            Say("Stopped for everyone. The room is still open.");
        }
    }

    /// <summary>Ends the room for everyone (the page asked first) and goes to Watch Party.</summary>
    public async Task EndRoomAsync()
    {
        if (await HostAsync(_party.EndRoomAsync, "Failed to end room"))
        {
            Say("Room ended");
            _navigation.NavigateTo(AppRoute.WatchParty);
        }
    }

    private bool CanAct() => !IsBusy && IsHost;

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
            Say(Failed(failure, ex), warning: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // The web's words for what failed; a server out of reach or busy says so too.
    private static string Failed(string failure, Exception ex) => ex switch
    {
        SiloApiException { Status: 429 or 503 } => $"{failure}. {LibraryErrors.Busy}",
        HttpRequestException or OperationCanceledException => $"{failure}. {LibraryErrors.Unreachable}",
        _ => failure,
    };

    // Said only while the player shows the panel: a player that left has nowhere to say it.
    private void Say(string text, bool warning = false)
    {
        if (_attached)
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
        TogglePolicyCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static readonly string[] RoomProperties =
    [
        nameof(IsHost), nameof(Code), nameof(ViewersText), nameof(PolicyText), nameof(PolicyButtonText), nameof(ConnectionState),
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
