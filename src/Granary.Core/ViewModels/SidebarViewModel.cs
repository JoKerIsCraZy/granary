using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>One library entry in the sidebar.</summary>
public sealed record SidebarLibrary(string Id, string Name, string Type);

/// <summary>
/// The sidebar shown once a profile is acting: the server's name and libraries on top and the profile at
/// the bottom, as in Silo's web client. It follows the session; a new server or profile reloads it.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly ISessionManager _session;
    private readonly ProfilesApi _profiles;
    private readonly CatalogApi _catalog;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SidebarViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loaded;
    private int _generation;

    public SidebarViewModel(
        ISessionManager session,
        ProfilesApi profiles,
        CatalogApi catalog,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        ILogger<SidebarViewModel> logger)
    {
        _session = session;
        _profiles = profiles;
        _catalog = catalog;
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

    /// <summary>The profile's libraries in the server's order.</summary>
    public ObservableCollection<SidebarLibrary> Libraries { get; } = [];

    /// <summary>The account administers the server: the sidebar offers Admin above Settings, as the web's menu does.</summary>
    [ObservableProperty]
    public partial bool IsAdmin { get; private set; }

    /// <summary>Loads what the current session shows; does nothing when that is already shown.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsAdmin = _session is { State: SessionState.Ready, Account.IsAdmin: true };
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_loaded == key)
        {
            return;
        }
        _loaded = key;
        int generation = ++_generation;
        ServerName = _session.ActiveServer?.Name ?? "";
        Profile = null;
        Libraries.Clear();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileInitials));
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server, ProfileId: { } profileId })
        {
            return;
        }
        var profileLoaded = LoadProfileAsync(server.BaseUri, profileId, generation, cancellationToken);
        var librariesLoaded = LoadLibrariesAsync(server.BaseUri, generation, cancellationToken);
        bool complete = await profileLoaded & await librariesLoaded;
        if (!complete)
        {
            // The next refresh tries again.
            _loaded = null;
        }
    }

    private async Task<bool> LoadProfileAsync(Uri server, string profileId, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var collection = await _profiles.ListAsync(server, cancellationToken);
            if (generation == _generation && collection.Items.FirstOrDefault(p => p.Id == profileId) is { } profile)
            {
                Profile = ProfileItemViewModel.From(profile, server);
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException)
        {
            // The account name stands in.
            _logger.LogWarning(ex, "Could not load the acting profile for the sidebar");
            return false;
        }
    }

    private async Task<bool> LoadLibrariesAsync(Uri server, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var collection = await _catalog.ListLibrariesAsync(server, cancellationToken);
            if (generation == _generation)
            {
                var ordered = collection.Items.OrderBy(l => l.SortOrder).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase);
                foreach (var library in ordered)
                {
                    Libraries.Add(new SidebarLibrary(library.Id, library.Name, library.Type));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException)
        {
            _logger.LogWarning(ex, "Could not load the libraries for the sidebar");
            return false;
        }
    }

    [RelayCommand]
    private Task SwitchProfileAsync() => _session.ClearProfileAsync();

    [RelayCommand]
    private Task SignOutAsync() => _session.SignOutAsync();

    /// <summary>The servers live in Settings → Servers, as the apps keep them under their settings.</summary>
    [RelayCommand]
    private void OpenServers() => _navigation.NavigateTo(AppRoute.Settings, "servers");

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
