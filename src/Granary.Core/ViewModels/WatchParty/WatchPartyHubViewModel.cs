using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The Watch Party hub, as the web's (<c>pages/watchtogether/WatchPartyHub.tsx</c>): open an empty room in which the
/// host picks or everyone votes, or join one with its code or invite link. Back in a room already, it leads there; a room that is over is said at
/// the top, where the room page and the player bring the viewer once it is. "Your recent parties" offers the rooms of the
/// last day back, each asked once whether it is still on.
/// </summary>
public sealed partial class WatchPartyHubViewModel : ObservableObject
{
    private readonly WatchPartySession _party;
    private readonly INavigationService _navigation;
    private readonly ILogger<WatchPartyHubViewModel> _logger;

    public WatchPartyHubViewModel(WatchPartySession party, INavigationService navigation, ILogger<WatchPartyHubViewModel> logger)
    {
        _party = party;
        _navigation = navigation;
        _logger = logger;
        _party.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WatchPartySession.Room) or nameof(WatchPartySession.End))
            {
                foreach (string name in (string[])[nameof(IsInRoom), nameof(CurrentRoomText), nameof(HasEnded), nameof(EndedTitle), nameof(EndedText),
                    nameof(CanRejoin)])
                {
                    OnPropertyChanged(name);
                }
            }
        };
    }

    /// <summary>The room's code or its invite link, as typed or pasted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    public partial string Code { get; set; } = "";

    /// <summary>
    /// How the room Start a party opens picks, as the web's hub offers it: Host picks ("You choose, everyone watches.") or
    /// Everyone votes ("Anyone suggests, the room votes."). Host picks first on every visit, as the web's choice lives
    /// with its page and the apps' plain "Start a party" is a host-pick room.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostPick), nameof(IsVoteMode))]
    public partial string SelectionMode { get; private set; } = WatchPartySelectionModes.HostPick;

    /// <summary>The Host picks card is chosen; choosing it is a radio's check (an uncheck says nothing).</summary>
    public bool IsHostPick
    {
        get => SelectionMode == WatchPartySelectionModes.HostPick;
        set
        {
            if (value)
            {
                SelectionMode = WatchPartySelectionModes.HostPick;
            }
        }
    }

    /// <summary>The Everyone votes card is chosen.</summary>
    public bool IsVoteMode
    {
        get => SelectionMode == WatchPartySelectionModes.Vote;
        set
        {
            if (value)
            {
                SelectionMode = WatchPartySelectionModes.Vote;
            }
        }
    }

    /// <summary>Why the last create or join failed, in the web's words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateLabel), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(JoinCommand))]
    public partial bool IsCreating { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinLabel), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(JoinCommand))]
    public partial bool IsJoining { get; private set; }

    public bool HasError => Error.Length > 0;

    public bool IsBusy => IsCreating || IsJoining;

    public string CreateLabel => IsCreating ? "Creating..." : "Create Watch Party";

    public string JoinLabel => IsJoining ? "Joining..." : "Join Watch Party";

    /// <summary>This profile is in a room already, one that is not over.</summary>
    public bool IsInRoom => _party.IsInRoom;

    public string CurrentRoomText => IsInRoom && _party.Room is { } room ? $"You're in Watch Party {room.Code}." : "";

    /// <summary>
    /// The room this device was in is over: the hub says why, as Silo's apps do once they left the room for it, in the words
    /// of the web's end of a room (<see cref="WatchPartyText.EndedTitle"/>).
    /// </summary>
    public bool HasEnded => _party.End is not null;

    public string EndedTitle => _party.End is { } end ? WatchPartyText.EndedTitle(end) : "";

    public string EndedText => _party.End is { } end ? WatchPartyText.EndedText(end) : "";

    /// <summary>Another device took this profile's place: it can claim the room back from here.</summary>
    public bool CanRejoin => _party.End?.Replaced == true;

    /// <summary>The hub shows again: it starts from Host picks.</summary>
    public void Arrive() => SelectionMode = WatchPartySelectionModes.HostPick;

    /// <summary>
    /// The join box's text as the web's box shows it: a code in capitals. An invite link keeps its case, since its token
    /// is case-sensitive.
    /// </summary>
    public static string AsTyped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("token=", StringComparison.OrdinalIgnoreCase) || text.Contains('/') ? text : text.ToUpperInvariant();
    }

    /// <summary>"Your recent parties": the rooms of the last day, newest first.</summary>
    public ObservableCollection<WatchPartyRecentRow> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    /// <summary>Reads the recent rooms again and asks each that has not ended whether it still is on.</summary>
    public async Task RefreshRecentAsync()
    {
        IReadOnlyList<WatchPartyRecentRoom> rooms;
        try
        {
            rooms = await _party.ListRecentAsync();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is IOException)
        {
            _logger.LogWarning("The recent Watch Parties could not be read: {Failure}", ex.GetType().Name);
            return;
        }
        Recent.Clear();
        foreach (var room in rooms)
        {
            Recent.Add(new WatchPartyRecentRow(room));
        }
        OnPropertyChanged(nameof(HasRecent));
        await Task.WhenAll(Recent.Where(r => !r.Room.Ended).ToList().Select(CheckAsync));
    }

    private async Task CheckAsync(WatchPartyRecentRow row)
    {
        var state = await _party.CheckRecentAsync(row.Room);
        if (state == WatchPartyRecentState.Gone)
        {
            Recent.Remove(row);
            OnPropertyChanged(nameof(HasRecent));
            return;
        }
        row.Status = state switch
        {
            WatchPartyRecentState.Live => WatchPartyRecentRow.LiveStatus,
            WatchPartyRecentState.Unknown => WatchPartyRecentRow.UnknownStatus,
            _ => WatchPartyRecentRow.EndedStatus,
        };
    }

    /// <summary>Back into a recent room with the proof it kept.</summary>
    [RelayCommand]
    private async Task RejoinRecentAsync(WatchPartyRecentRow? row)
    {
        if (row is not { CanRejoin: true } || IsBusy)
        {
            return;
        }
        IsJoining = true;
        Error = "";
        try
        {
            await _party.ReturnAsync(row.Room);
            _navigation.NavigateTo(AppRoute.WatchPartyRoom);
        }
        catch (WatchPartyStaleException)
        {
            _logger.LogDebug("A recent Watch Party opened for the profile before was not entered");
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not go back into a recent Watch Party");
            row.Status = ex is SiloApiException { Status: 403 or 404 or 409 or 410 } ? WatchPartyRecentRow.EndedStatus : row.Status;
            Error = ex is SiloApiException { Status: 404 or 409 or 410 or 403 } ? "That room is no longer active." : "Failed to join room.";
        }
        finally
        {
            IsJoining = false;
        }
    }

    partial void OnCodeChanged(string value) => Error = "";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CreateAsync()
    {
        IsCreating = true;
        Error = "";
        try
        {
            await _party.CreateAsync(SelectionMode);
            _navigation.NavigateTo(AppRoute.WatchPartyRoom);
        }
        catch (WatchPartyStaleException)
        {
            // The profile or server changed meanwhile: the room it asked for is no longer this page's to show.
            _logger.LogDebug("A Watch Party opened for the profile before was not entered");
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open a Watch Party");
            Error = ex is SiloApiException api && api.Problem.Detail is { Length: > 0 } detail ? detail : "Failed to create room.";
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync()
    {
        IsJoining = true;
        Error = "";
        try
        {
            await _party.JoinAsync(Code);
            Code = "";
            _navigation.NavigateTo(AppRoute.WatchPartyRoom);
        }
        catch (WatchPartyStaleException)
        {
            // The profile or server changed meanwhile: the room it asked for is no longer this page's to show.
            _logger.LogDebug("A Watch Party joined for the profile before was not entered");
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not join a Watch Party");
            Error = ex switch
            {
                SiloApiException { Status: 404 } => "Room not found.",
                SiloApiException { Status: 409 or 410 } => "That room is no longer active.",
                _ => "Failed to join room.",
            };
        }
        finally
        {
            IsJoining = false;
        }
    }

    /// <summary>Back to the room this profile is in.</summary>
    [RelayCommand]
    private void OpenRoom()
    {
        if (_party.IsInRoom)
        {
            _navigation.NavigateTo(AppRoute.WatchPartyRoom);
        }
    }

    /// <summary>"Rejoin Watch Party": claims the room back from the device that took it over, and goes there.</summary>
    [RelayCommand]
    private void Rejoin()
    {
        if (!CanRejoin)
        {
            return;
        }
        _party.Rejoin();
        _navigation.NavigateTo(AppRoute.WatchPartyRoom);
    }

    /// <summary>Puts the end away: the room it was about is left on this device.</summary>
    [RelayCommand]
    private Task DismissEndAsync() => HasEnded ? _party.LeaveAsync() : Task.CompletedTask;

    private bool CanStart() => !IsBusy;

    private bool CanJoin() => !IsBusy && Code.Trim().Length > 0;
}
