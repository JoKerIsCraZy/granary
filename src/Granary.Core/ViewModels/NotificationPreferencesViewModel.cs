using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Notifications;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The inbox's Preferences, as the web's popover: the master switch, then Favorites, Watchlist, Continue Watching and
/// Next Up, which only count while it is on. A switch saves at once; one that fails to save goes back and says so.
/// </summary>
public sealed partial class NotificationPreferencesViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly NotificationsApi _notifications;
    private readonly ILogger _logger;
    private bool _showing;
    private int _saves;

    internal NotificationPreferencesViewModel(ISessionManager session, NotificationsApi notifications, ILogger logger)
    {
        _session = session;
        _notifications = notifications;
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>"Couldn't save. Try again." after a switch failed.</summary>
    [ObservableProperty]
    public partial string SaveError { get; private set; } = "";

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool NotifyFavorites { get; set; }

    [ObservableProperty]
    public partial bool NotifyWatchlist { get; set; }

    [ObservableProperty]
    public partial bool NotifyContinueWatching { get; set; }

    [ObservableProperty]
    public partial bool NotifyNextUp { get; set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>Reads the switches, each time the popover opens.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        State = LibraryLoadState.Loading;
        SaveError = "";
        try
        {
            Show(await _notifications.GetPreferencesAsync(server.BaseUri));
            State = LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            // Also called from a failed save, where nothing may escape.
            _logger.LogWarning(ex, "Could not read the notification preferences");
            State = LibraryLoadState.Error;
        }
    }

    partial void OnEnabledChanged(bool value) => Save(new NotificationPreferencesChange { Enabled = value });

    partial void OnNotifyFavoritesChanged(bool value) => Save(new NotificationPreferencesChange { NotifyFavorites = value });

    partial void OnNotifyWatchlistChanged(bool value) => Save(new NotificationPreferencesChange { NotifyWatchlist = value });

    partial void OnNotifyContinueWatchingChanged(bool value) => Save(new NotificationPreferencesChange { NotifyContinueWatching = value });

    partial void OnNotifyNextUpChanged(bool value) => Save(new NotificationPreferencesChange { NotifyNextUp = value });

    // A switch the viewer flipped; the server's answer is shown as it is.
    private async void Save(NotificationPreferencesChange change)
    {
        if (_showing || State != LibraryLoadState.Ready || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        SaveError = "";
        // Only the latest save's answer is shown: an earlier one would undo a switch flipped after it.
        int save = ++_saves;
        try
        {
            var saved = await _notifications.UpdatePreferencesAsync(server.BaseUri, change);
            if (save == _saves)
            {
                Show(saved);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. The latest save's failure puts back what the server has.
            _logger.LogWarning(ex, "Could not save a notification preference");
            if (save == _saves)
            {
                await LoadAsync();
            }
            SaveError = "Couldn't save. Try again.";
        }
    }

    private void Show(NotificationPreferences preferences)
    {
        _showing = true;
        try
        {
            Enabled = preferences.Enabled;
            NotifyFavorites = preferences.NotifyFavorites;
            NotifyWatchlist = preferences.NotifyWatchlist;
            NotifyContinueWatching = preferences.NotifyContinueWatching;
            NotifyNextUp = preferences.NotifyNextUp;
        }
        finally
        {
            _showing = false;
        }
    }
}
