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
/// the bottom, as in Silo's web client. It follows the session; a new server or profile reloads it. What could not be
/// read is asked for again after <see cref="RetryAfter"/>, and the rest once it is <see cref="StaleAfter"/> old, so a
/// library the profile hid elsewhere goes; the shown list stays while it is read again.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    /// <summary>A read that failed (the libraries, the profile, or its hidden libraries) is tried again after this.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>As the web's staleTime for the library list and its preferences.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly ISessionManager _session;
    private readonly ProfilesApi _profiles;
    private readonly VisibleLibraries _libraries;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<SidebarViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loaded;
    private (string? ServerId, string? ProfileId)? _shown;
    private int _generation;
    private ITimer? _readAgain;

    public SidebarViewModel(
        ISessionManager session,
        ProfilesApi profiles,
        VisibleLibraries libraries,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<SidebarViewModel> logger)
    {
        _session = session;
        _profiles = profiles;
        _libraries = libraries;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
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

    /// <summary>The libraries the profile browses: its hidden ones left out, in its order or else the server's.</summary>
    public ObservableCollection<SidebarLibrary> Libraries { get; } = [];

    /// <summary>The libraries were read for this server and profile: an empty list then means it browses none.</summary>
    [ObservableProperty]
    public partial bool AreLibrariesLoaded { get; private set; }

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
        if (_shown != key)
        {
            // Another server or profile: nothing of the last one stays while this one's is read.
            _shown = key;
            Profile = null;
            Libraries.Clear();
            AreLibrariesLoaded = false;
            OnPropertyChanged(nameof(ProfileName));
            OnPropertyChanged(nameof(ProfileInitials));
        }
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server, ProfileId: { } profileId })
        {
            return;
        }
        bool complete = false;
        try
        {
            var profileLoaded = LoadProfileAsync(server.BaseUri, profileId, generation, cancellationToken);
            var librariesLoaded = LoadLibrariesAsync(server.BaseUri, generation, cancellationToken);
            complete = (await Task.WhenAll(profileLoaded, librariesLoaded)).All(loaded => loaded);
        }
        finally
        {
            // A refresh a newer one overtook (another profile, say) leaves the outcome to that one.
            if (generation == _generation)
            {
                if (!complete)
                {
                    // The next refresh tries again.
                    _loaded = null;
                }
                ReadAgainIn(complete ? StaleAfter : RetryAfter);
            }
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
        catch (Exception ex) when (IsFailedRead(ex, cancellationToken))
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
            var libraries = await _libraries.ListAsync(server, cancellationToken);
            if (generation == _generation)
            {
                // Read again in place: the entries change only where the list did.
                var shown = libraries.Items.Select(library => new SidebarLibrary(library.Id, library.Name, library.Type)).ToList();
                if (!shown.SequenceEqual(Libraries))
                {
                    Libraries.Clear();
                    shown.ForEach(Libraries.Add);
                }
                AreLibrariesLoaded = true;
            }
            // Without the profile's hidden libraries every one shows for now; they are asked for again.
            return libraries.PreferencesRead;
        }
        catch (Exception ex) when (IsFailedRead(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Could not load the libraries for the sidebar");
            return false;
        }
    }

    // A refusal, no connection, or a timeout: the caller's own cancelling is not a failure to try again.
    private static bool IsFailedRead(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SiloApiException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void ReadAgainIn(TimeSpan delay)
    {
        _readAgain?.Dispose();
        _readAgain = _time.CreateTimer(_ => _dispatcher.Post(ReadAgain), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void ReadAgain()
    {
        _loaded = null;
        _ = RefreshSafelyAsync();
    }

    [RelayCommand]
    private Task SwitchProfileAsync() => _session.ClearProfileAsync();

    [RelayCommand]
    private Task SignOutAsync() => _session.SignOutAsync();

    /// <summary>The servers live in Settings → Servers, as the apps keep them under their settings.</summary>
    [RelayCommand]
    private void OpenServers() => _navigation.NavigateTo(AppRoute.Settings, "servers");

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _readAgain?.Dispose();
    }

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
