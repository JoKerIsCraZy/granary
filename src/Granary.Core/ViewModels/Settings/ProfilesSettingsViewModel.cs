using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Profiles, as the web's <c>pages/settings/ProfilesSettings.tsx</c>: the household's profiles with their badges
/// and limits, New profile while there is room, and each profile's Edit and Delete. Only the household's primary profile
/// sees the page (docs/api-contract.md, 6.7), and its writes go out as that profile. What changes here, the sidebar's
/// profile entry and menu read again.
/// </summary>
public sealed partial class ProfilesSettingsViewModel : ObservableObject
{
    private readonly ProfilesApi _profiles;
    private readonly ISessionManager _session;
    private readonly IConfirmation _confirmation;
    private readonly SidebarViewModel? _sidebar;
    private readonly ILogger<ProfilesSettingsViewModel> _logger;

    // The server, account and acting profile the list was read for; a write goes out only while they still are.
    private (ServerEntry Server, string AccountId, string ProfileId)? _target;
    private int _load;

    public ProfilesSettingsViewModel(ProfilesApi profiles, ISessionManager session, IConfirmation confirmation, ILogger<ProfilesSettingsViewModel> logger,
        SidebarViewModel? sidebar = null)
    {
        _profiles = profiles;
        _session = session;
        _confirmation = confirmation;
        _logger = logger;
        _sidebar = sidebar;
    }

    public ObservableCollection<ProfileSettingsRow> Profiles { get; } = [];

    /// <summary>The household as last read, with what a profile can carry; null before the first read.</summary>
    public ProfileCollection? Household { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsList))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The list was read: an empty one then means the account has no profiles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowsList))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>"No profiles yet", as the web's empty list.</summary>
    public bool IsEmpty => IsLoaded && Profiles.Count == 0;

    public bool ShowsList => IsLoaded && !IsLoading;

    /// <summary>The last outcome, as the web's toasts say it ("Profile deleted"), or why the list could not be read.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>The list could not be read; the page offers Retry.</summary>
    [ObservableProperty]
    public partial bool HasLoadError { get; private set; }

    /// <summary>New profile shows: the household has room, as far as the server said.</summary>
    [ObservableProperty]
    public partial bool CanAdd { get; private set; }

    /// <summary>"This account can have 5 profiles. An administrator can raise the limit." once the household is full.</summary>
    [ObservableProperty]
    public partial string LimitText { get; private set; } = "";

    /// <summary>Reads the household for the server and profile in use.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int load = ++_load;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server, Account.Id: { } accountId, ProfileId: { } profileId })
        {
            _target = null;
            Show(null, null);
            return;
        }
        IsLoading = true;
        HasLoadError = false;
        try
        {
            var household = await _profiles.ListAsync(server.BaseUri, cancellationToken);
            if (load != _load)
            {
                return;
            }
            _target = (server, accountId, profileId);
            Show(household, server.BaseUri);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (load != _load)
            {
                return;
            }
            _logger.LogWarning("Settings → Profiles could not read the household: {Failure}", ApiFailureText.Describe(ex));
            Status = LibraryErrors.Describe(ex);
            HasLoadError = true;
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Status = "";
        return LoadAsync();
    }

    /// <summary>
    /// Deletes a profile after the web's question ("Delete profile "Elena"? This action cannot be undone."). One that is gone
    /// already leaves the list too; any other failure keeps it and says so.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteAsync(ProfileSettingsRow? row)
    {
        if (row is null || row.IsBusy || !row.CanDelete || Target() is not { } target || !Profiles.Contains(row))
        {
            return;
        }
        bool asked = await _confirmation.AskAsync(AppText.Get("web.settings.profiles.deleteTitle"),
            AppText.Get("web.settings.profiles.deleteDescription", ("name", row.Name)), AppText.Get("web.settings.profiles.delete"));
        if (!asked || Target() is not { } now || now != target || !Profiles.Contains(row))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _profiles.DeleteAsync(target.Server.BaseUri, row.Id, ManagingProfile.Acting(target.AccountId, target.ProfileId));
            Status = AppText.Get("web.common.toast.profiles.deleted");
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Deleted elsewhere meanwhile.
            Status = AppText.Get("web.common.toast.profiles.deleted");
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ProfileChangedException)
        {
            _logger.LogWarning("A profile could not be deleted: {Failure}", ApiFailureText.Describe(ex));
            Status = AppText.Get("web.common.toast.profiles.deleteFailed");
            return;
        }
        finally
        {
            row.IsBusy = false;
        }
        await AfterChangeAsync();
    }

    /// <summary>The list and the sidebar read the household again, as the web refetches it after a write.</summary>
    private async Task AfterChangeAsync()
    {
        _sidebar?.ReadAgain();
        string status = Status;
        await LoadAsync();
        if (!HasLoadError)
        {
            Status = status;
        }
    }

    // The server and profile the list belongs to, while they are still the ones in use.
    private (ServerEntry Server, string AccountId, string ProfileId)? Target() =>
        _target is { } target && _session.State == SessionState.Ready && _session.ActiveServer?.Id == target.Server.Id
            && _session.Account?.Id == target.AccountId && _session.ProfileId == target.ProfileId
            ? target
            : null;

    private void Show(ProfileCollection? household, Uri? serverBase)
    {
        Household = household;
        Profiles.Clear();
        if (household is not null && serverBase is not null)
        {
            string? acting = _target?.ProfileId;
            foreach (var profile in household.Items)
            {
                Profiles.Add(new ProfileSettingsRow(profile, serverBase, profile.Id == acting,
                    ProfileSettingsRow.GuardOf(profile, household.Items.Count, acting)));
            }
        }
        IsLoaded = household is not null;
        CanAdd = household?.HasRoom == true;
        LimitText = household is { HasRoom: false, MaxProfiles: { } limit }
            ? AppText.Get("web.settings.profiles.limitReached", ("count", limit))
            : "";
        OnPropertyChanged(nameof(IsEmpty));
    }
}
