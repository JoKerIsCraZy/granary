using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The sidebar shown once a profile is acting: the server's name on top and the profile at the bottom,
/// as in Silo's web client. It follows the session; a new server or profile reloads it.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly ISessionManager _session;
    private readonly ProfilesApi _profiles;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SidebarViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loaded;
    private int _generation;

    public SidebarViewModel(
        ISessionManager session, ProfilesApi profiles, INavigationService navigation, IUiDispatcher dispatcher, ILogger<SidebarViewModel> logger)
    {
        _session = session;
        _profiles = profiles;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _logger = logger;
        _session.Changed += OnSessionChanged;
    }

    [ObservableProperty]
    public partial string ServerName { get; private set; } = "";

    /// <summary>The acting profile; null until it is loaded, or when the list could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileName), nameof(ProfileInitials))]
    public partial ProfileItemViewModel? Profile { get; private set; }

    /// <summary>The profile's name, or the account's while the profile is unknown.</summary>
    public string ProfileName => Profile?.Name ?? _session.Account?.Username ?? "";

    public string ProfileInitials => Profile?.Initials ?? ProfileItemViewModel.InitialsOf(ProfileName);

    /// <summary>Loads what the current session shows; does nothing when that is already shown.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_loaded == key)
        {
            return;
        }
        _loaded = key;
        int generation = ++_generation;
        ServerName = _session.ActiveServer?.Name ?? "";
        Profile = null;
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileInitials));
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server, ProfileId: { } profileId })
        {
            return;
        }
        try
        {
            var collection = await _profiles.ListAsync(server.BaseUri, cancellationToken);
            if (generation == _generation && collection.Items.FirstOrDefault(p => p.Id == profileId) is { } profile)
            {
                Profile = ProfileItemViewModel.From(profile, server.BaseUri);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException)
        {
            // The account name stands in; the next session change tries again.
            _logger.LogWarning(ex, "Could not load the acting profile for the sidebar");
            _loaded = null;
        }
    }

    [RelayCommand]
    private Task SwitchProfileAsync() => _session.ClearProfileAsync();

    [RelayCommand]
    private Task SignOutAsync() => _session.SignOutAsync();

    [RelayCommand]
    private void OpenServers() => _navigation.NavigateTo(AppRoute.Servers);

    public void Dispose() => _session.Changed -= OnSessionChanged;

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => _ = RefreshSafelyAsync());

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not refresh the sidebar");
        }
    }
}
