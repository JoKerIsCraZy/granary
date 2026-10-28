using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// The Watch Party hub, as the web's (<c>pages/watchtogether/WatchPartyHub.tsx</c>): open an empty room the host picks
/// for, or join one with its code or invite link. Back in a room already, it leads there. "Your recent parties" offers
/// the rooms of the last day back, each asked once whether it is still on.
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
                OnPropertyChanged(nameof(IsInRoom));
                OnPropertyChanged(nameof(CurrentRoomText));
            }
        };
    }

    /// <summary>The room's code or its invite link, as typed or pasted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    public partial string Code { get; set; } = "";

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

    /// <summary>This profile is in a room already.</summary>
    public bool IsInRoom => _party.Room is not null;

    public string CurrentRoomText => _party.Room is { } room ? $"You're in Watch Party {room.Code}." : "";

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
            await _party.CreateAsync();
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
        if (_party.Room is not null)
        {
            _navigation.NavigateTo(AppRoute.WatchPartyRoom);
        }
    }

    private bool CanStart() => !IsBusy;

    private bool CanJoin() => !IsBusy && Code.Trim().Length > 0;
}
